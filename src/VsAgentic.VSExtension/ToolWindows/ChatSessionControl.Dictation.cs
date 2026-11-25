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

    // The bars beside the microphone: each tick the newest level enters on the
    // right and the rest move left, so the row reads as a moving waveform.
    private const int MeterBars = 9;
    private const double MeterMin = 3, MeterMax = 18;
    private readonly System.Windows.Threading.DispatcherTimer _meterTimer =
        new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(55) };
    private readonly double[] _meterLevels = new double[MeterBars];
    private System.Windows.Shapes.Rectangle[]? _meterBars;

    private void StartMeter()
    {
        if (_meterBars is null)
        {
            _meterBars = new System.Windows.Shapes.Rectangle[MeterBars];
            for (var i = 0; i < MeterBars; i++)
            {
                _meterBars[i] = new System.Windows.Shapes.Rectangle
                {
                    Width = 3,
                    Height = MeterMin,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = Brushes.Red,
                };
                DictationMeter.Children.Add(_meterBars[i]);
            }
            _meterTimer.Tick += (_, _) => TickMeter();
        }

        Array.Clear(_meterLevels, 0, _meterLevels.Length);
        DictationMeter.Visibility = Visibility.Visible;
        _meterTimer.Start();
    }

    private void StopMeter()
    {
        _meterTimer.Stop();
        DictationMeter.Visibility = Visibility.Collapsed;
    }

    private void TickMeter()
    {
        var level = _recorder?.Level ?? 0;
        // Speech peaks at a fraction of full scale; the root lifts quiet speech
        // so the bars move for a normal voice, not only a loud one.
        var shaped = Math.Min(1.0, Math.Sqrt(level) * 1.6);

        Array.Copy(_meterLevels, 1, _meterLevels, 0, MeterBars - 1);
        _meterLevels[MeterBars - 1] = shaped;
        for (var i = 0; i < MeterBars; i++)
            _meterBars![i].Height = MeterMin + (MeterMax - MeterMin) * _meterLevels[i];
    }

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

        var recorder = new WaveRecorder();
        try
        {
            _recorder = recorder;
            // Only this recording may be ended: by the time the dispatcher runs
            // this, it may be finished and a new one started.
            recorder.LimitReached += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_recorder, recorder) && recorder.IsRecording) FinishDictation();
            }));
            recorder.Start();
        }
        catch (SpeechToTextException ex)
        {
            recorder.Dispose();
            _recorder = null;
            ShowDictationError(ex.Message);
            InputTextBox.Focus();
            return;
        }

        SetDictationState(recording: true);
        InputTextBox.Focus();
    }

    // The window is closed (from Shutdown, not Unloaded, which tabbing away
    // also raises): nothing may keep recording or turning bars. Safe to call
    // twice and with no recording. No focus here: the window is going away.
    private void AbortDictation()
    {
        try { _transcribeCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        var recorder = _recorder;
        _recorder = null;
        if (recorder is not null)
        {
            try { recorder.Stop(); }
            catch (Exception) { }
            try { recorder.Dispose(); }
            catch (Exception) { }
        }

        if (!_transcribing) SetDictationState(recording: false);
    }

    private async void FinishDictation()
    {
        var recorder = _recorder;
        if (recorder is null) return;
        _recorder = null;

        byte[] wav;
        try
        {
            wav = recorder.Stop();
        }
        catch (ObjectDisposedException)
        {
            SetDictationState(recording: false);
            InputTextBox.Focus();
            return;
        }
        finally
        {
            recorder.Dispose();
        }

        var settings = VsAgenticPackage.GetSpeechSettings();
        var service = SpeechToTextFactory.Create(settings);

        // Under a second is a mis-click, not a prompt: 44-byte header + 16000 B/s * 2.
        if (service is null || wav.Length < 44 + 32000)
        {
            SetDictationState(recording: false);
            InputTextBox.Focus();
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
            // The button was disabled meanwhile; the shortcut lives on the box.
            InputTextBox.Focus();
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
        // Room for the microphone alone at rest; the level bars need more while recording.
        InputTextBox.Padding = new Thickness(8, 6, recording ? 84 : 44, 6);
        if (recording)
        {
            DictationGlyph.Foreground = Brushes.Red;
            StartMeter();
        }
        else
        {
            StopMeter();
            DictationGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                Microsoft.VisualStudio.Shell.VsBrushes.WindowTextKey);
        }

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
