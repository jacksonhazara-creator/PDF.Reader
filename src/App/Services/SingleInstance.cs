using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PdfReader.App.Services;

/// <summary>
/// One window, many tabs: a second launch (e.g. double-clicking another PDF) hands its files to
/// the running instance over a named pipe restricted to the current user, then exits.
/// </summary>
internal static partial class SingleInstance
{
    private static Mutex? s_mutex;
    private static readonly string s_id = ComputeId();
    private static string PipeName => $"PDF.Reader.{s_id}";

    [LibraryImport("user32.dll")]
    private static partial int AllowSetForegroundWindow(int processId);

    /// <returns>true if this process should start the UI; false if the files were forwarded.</returns>
    public static bool TryBecomePrimary(string[] files)
    {
        s_mutex = new Mutex(initiallyOwned: true, $"Local\\PDF.Reader.{s_id}", out var created);
        if (created)
            return true;

        try
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: let the primary window come to the front
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            foreach (var file in files)
                writer.WriteLine(Path.GetFullPath(file));
            writer.WriteLine();
            writer.Flush();
            return false;
        }
        catch (Exception)
        {
            return true; // primary is hung or gone: start normally rather than doing nothing
        }
    }

    public static void StartServer(Action<string[]> onActivated)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var files = new List<string>();
                    while (await reader.ReadLineAsync() is { Length: > 0 } line && files.Count < 100)
                        files.Add(line);
                    onActivated(files.ToArray());
                }
                catch (Exception)
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    private static string ComputeId()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)), 0, 8);
    }
}
