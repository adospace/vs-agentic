using System.IO;
using System.Runtime.InteropServices;

namespace VsAgentic.Services.Dictation;

/// <summary>
/// Records the default microphone as 16 kHz mono 16-bit PCM through winmm, so
/// the extension needs no audio library. <see cref="Stop"/> returns a WAV file.
/// </summary>
public sealed class WaveRecorder : IDisposable
{
    private const int SampleRate = 16000;
    private const int BufferBytes = SampleRate * 2 / 5;   // 200 ms
    private const int BufferCount = 4;
    // Keeps Azure's 60 s limit and a forgotten recording from growing without end.
    public const int MaxSeconds = 55;

    private readonly object _gate = new();
    private readonly MemoryStream _pcm = new();
    private readonly WaveInProc _callback;
    private readonly List<(IntPtr Header, IntPtr Data)> _buffers = new();
    private IntPtr _device;
    private volatile bool _recording;
    private volatile bool _limitHit;
    // Set under lock(_pcm) before the buffers are freed. The callback reads
    // the headers only under the same lock and only while this is false.
    private bool _released = true;

    /// <summary>Raised once, on a worker thread, when the recording reached <see cref="MaxSeconds"/>.</summary>
    public event Action? LimitReached;

    /// <summary>Peak level of the last buffer, 0..1. For a level indicator.</summary>
    public double Level { get; private set; }

    public WaveRecorder()
    {
        // Held in a field: the native side calls it, and a collected delegate would crash.
        _callback = OnWaveIn;
    }

    public bool IsRecording => _recording;

    public void Start()
    {
        lock (_gate)
        {
            if (_recording) return;
            _limitHit = false;
            lock (_pcm)
            {
                _pcm.SetLength(0);
                _released = false;
            }

            var format = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = SampleRate * 2,
                nBlockAlign = 2,
                wBitsPerSample = 16,
                cbSize = 0,
            };

            var result = waveInOpen(out _device, -1, ref format, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
            if (result != 0)
                throw new SpeechToTextException(result == 2 /* MMSYSERR_BADDEVICEID */ || waveInGetNumDevs() == 0
                    ? "No microphone was found."
                    : "The microphone could not be opened (error " + result + "). Another program may be using it.");

            var headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            for (var i = 0; i < BufferCount; i++)
            {
                var data = Marshal.AllocHGlobal(BufferBytes);
                var header = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = BufferBytes }, header, false);
                waveInPrepareHeader(_device, header, headerSize);
                waveInAddBuffer(_device, header, headerSize);
                _buffers.Add((header, data));
            }

            _recording = true;
            waveInStart(_device);
        }
    }

    /// <summary>Stops and returns the recording as a WAV file; empty when nothing was recorded.</summary>
    public byte[] Stop()
    {
        lock (_gate)
        {
            if (!_recording) return Array.Empty<byte>();
            // Under the callback's lock: a callback in progress finishes its
            // waveInAddBuffer first, and none after this gives a buffer back.
            lock (_pcm) _recording = false;

            // Reset hands every queued buffer back through the callback, so the
            // last audio is normally in _pcm before the headers are released.
            // That the callback has run by the time waveInReset returns is not
            // guaranteed, so a late one is shut out by _released instead. The
            // lock is not held across winmm calls: waveInReset or waveInClose
            // may wait for a callback that is waiting for the lock.
            waveInReset(_device);
            lock (_pcm) _released = true;
            var headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            foreach (var (header, data) in _buffers)
            {
                waveInUnprepareHeader(_device, header, headerSize);
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
            }
            _buffers.Clear();
            waveInClose(_device);
            _device = IntPtr.Zero;
            Level = 0;

            byte[] pcm;
            lock (_pcm) pcm = _pcm.ToArray();
            return pcm.Length == 0 ? Array.Empty<byte>() : ToWav(pcm);
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* closing anyway */ }
        lock (_pcm)
        {
            _released = true;
            _pcm.Dispose();
        }
    }

    private void OnWaveIn(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        if (msg != WIM_DATA) return;

        // A native winmm thread: an exception here would take Visual Studio down.
        try
        {
            // The callback runs on a winmm thread, while Stop may be waiting for
            // it inside waveInReset with the gate held. Lock _pcm, not the gate.
            lock (_pcm)
            {
                // Stopped and freed, or disposed: the header is no longer ours.
                if (_released) return;

                var header = Marshal.PtrToStructure<WAVEHDR>(param1);
                var count = (int)header.dwBytesRecorded;
                if (count > 0)
                {
                    var chunk = new byte[count];
                    Marshal.Copy(header.lpData, chunk, 0, count);

                    var peak = 0;
                    for (var i = 0; i + 1 < count; i += 2)
                        peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(chunk, i)));
                    Level = peak / 32768.0;

                    _pcm.Write(chunk, 0, count);
                }

                if (!_recording || _limitHit) return;

                if (_pcm.Length >= (long)SampleRate * 2 * MaxSeconds)
                {
                    // Stop must not run on this thread: it waits for this callback.
                    // No buffer goes back, so the recording ends; the owner calls Stop.
                    _limitHit = true;
                    System.Threading.Tasks.Task.Run(() => LimitReached?.Invoke());
                    return;
                }

                waveInAddBuffer(hwi, param1, Marshal.SizeOf(typeof(WAVEHDR)));
            }
        }
        catch
        {
            // Nothing can be reported from here; the buffer is simply lost.
        }
    }

    private static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + pcm.Length);
        w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    // ---- winmm ----------------------------------------------------------

    private const int CALLBACK_FUNCTION = 0x00030000;
    private const uint WIM_DATA = 0x3C0;

    private delegate void WaveInProc(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr hwi, int deviceId, ref WAVEFORMATEX format, WaveInProc callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hwi, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hwi);
}
