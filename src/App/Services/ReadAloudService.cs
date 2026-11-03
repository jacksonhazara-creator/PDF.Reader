using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace PdfReader.App.Services;

/// <summary>Reads text with the offline voices built into Windows (no network, no API keys).</summary>
public sealed partial class ReadAloudService : ObservableObject
{
    private SpeechSynthesizer? _synthesizer;
    private MediaPlayer? _player;
    private CancellationTokenSource? _cts;

    [ObservableProperty] public partial bool IsSpeaking { get; set; }

    /// <summary>Raised when speech is unavailable or the page has no text (message key).</summary>
    public event Action<string>? Problem;

    public void SpeakText(string text) => _ = RunAsync(async ct => await SpeakAsync(text, ct));

    /// <summary>Reads page after page until the end of the document or <see cref="Stop"/>.</summary>
    public void SpeakPages(Func<int, CancellationToken, Task<string>> getText, int startPage, int pageCount, Action<int> onPage)
        => _ = RunAsync(async ct =>
        {
            var spokeAnything = false;
            for (var page = startPage; page < pageCount; page++)
            {
                var text = await getText(page, ct);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (page != startPage)
                    Dispatcher.UIThread.Post(() => onPage(page));
                spokeAnything = true;
                await SpeakAsync(text, ct);
            }
            if (!spokeAnything)
                Problem?.Invoke("NoTextOnPage");
        });

    public void Stop()
    {
        _cts?.Cancel();
        _player?.Pause();
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        Stop();
        var cts = _cts = new CancellationTokenSource();
        IsSpeaking = true;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Problem?.Invoke("SpeechUnavailable");
        }
        finally
        {
            if (_cts == cts)
                IsSpeaking = false;
        }
    }

    private async Task SpeakAsync(string text, CancellationToken ct)
    {
        _synthesizer ??= new SpeechSynthesizer();
        using var stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
        ct.ThrowIfCancellationRequested();

        _player ??= new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnded(MediaPlayer sender, object args) => done.TrySetResult();
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => done.TrySetException(new InvalidOperationException(args.ErrorMessage));

        _player.MediaEnded += OnEnded;
        _player.MediaFailed += OnFailed;
        try
        {
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
            using (ct.Register(() => done.TrySetCanceled(ct)))
                await done.Task;
        }
        finally
        {
            _player.MediaEnded -= OnEnded;
            _player.MediaFailed -= OnFailed;
            if (ct.IsCancellationRequested)
                _player.Pause();
        }
    }
}
