using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.Services.Dictation;

namespace VsAgentic.VSExtension.ToolWindows;

public partial class ChatSessionControl
{
    private WaveRecorder? _recorder;
    private bool _transcribing;
    private CancellationTokenSource? _transcribeCts;

    private void DictationButton_Click(object sender, RoutedEventArgs e) => ToggleDictation();

    private void ToggleDictation()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_transcribing) return;
        if (_recorder is { IsRecording: true })
        {
            FinishDictation();
            return;
        }

        var settings = VsAgenticPackage.GetSpeechSettings();
        if (SpeechToTextFactory.Create(settings) is null)
        {
            // Not set up yet: the page where it is done is the useful answer.
            VsAgenticPackage.ShowDictationOptions();
            return;
        }

        try
        {
            _recorder = new WaveRecorder();
            _recorder.LimitReached += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_recorder is { IsRecording: true }) FinishDictation();
            }));
            _recorder.Start();
        }
        catch (SpeechToTextException ex)
        {
            _recorder?.Dispose();
            _recorder = null;
            ShowDictationError(ex.Message);
            return;
        }

        SetDictationState(recording: true);
        InputTextBox.Focus();
    }

    private async void FinishDictation()
    {
        var recorder = _recorder;
        if (recorder is null) return;

        var wav = recorder.Stop();
        recorder.Dispose();
        _recorder = null;

        var settings = VsAgenticPackage.GetSpeechSettings();
        var service = SpeechToTextFactory.Create(settings);

        // Under a second is a mis-click, not a prompt: 44-byte header + 16000 B/s * 2.
        if (service is null || wav.Length < 44 + 32000)
        {
            SetDictationState(recording: false);
            return;
        }

        // The caret is fixed now: typing while the service works must not move the insertion point.
        var caret = Math.Min(InputTextBox.CaretIndex, (InputTextBox.Text ?? "").Length);

        _transcribing = true;
        SetDictationState(recording: false, working: true);
        _transcribeCts = new CancellationTokenSource();
        try
        {
            var text = await service.TranscribeAsync(wav, settings.Language, _transcribeCts.Token);
            InsertDictatedText(text, caret);
        }
        catch (OperationCanceledException) { }
        catch (SpeechToTextException ex)
        {
            ShowDictationError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowDictationError("Dictation failed: " + ex.Message);
        }
        finally
        {
            _transcribing = false;
            _transcribeCts?.Dispose();
            _transcribeCts = null;
            SetDictationState(recording: false);
        }
    }

    private void InsertDictatedText(string text, int caret)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        var current = InputTextBox.Text ?? "";
        caret = Math.Min(caret, current.Length);

        // Words dictated one after another must not run together.
        var before = caret > 0 && !char.IsWhiteSpace(current[caret - 1]) ? " " : "";
        var insert = before + text;

        InputTextBox.Text = current.Insert(caret, insert);
        InputTextBox.CaretIndex = caret + insert.Length;
        InputTextBox.Focus();
    }

    private void SetDictationState(bool recording, bool working = false)
    {
        if (recording)
            DictationGlyph.Foreground = Brushes.Red;
        else
            DictationGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                Microsoft.VisualStudio.Shell.VsBrushes.WindowTextKey);
        DictationButton.ToolTip = recording ? "Stop and insert the text (Ctrl+Shift+M)"
            : working ? "Turning the recording into text…"
            : "Dictate (Ctrl+Shift+M)";
        DictationButton.IsEnabled = !working;
    }

    private void ShowDictationError(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(
            ServiceProvider.GlobalProvider,
            message,
            "Dictation",
            OLEMSGICON.OLEMSGICON_WARNING,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
