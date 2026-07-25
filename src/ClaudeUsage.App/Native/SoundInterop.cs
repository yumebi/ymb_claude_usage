using System.Runtime.InteropServices;

namespace ClaudeUsage.App.Native;

/// <summary>
/// メモリ上のWAVを鳴らす。
///
/// System.Media.SoundPlayer は .NET Core では System.Windows.Extensions の
/// 追加参照が要るため、依存を増やさずこのアプリの他のP/Invokeに合わせている。
/// </summary>
internal static class SoundInterop
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>
    /// アンマネージドメモリ上のWAVを非同期再生する。
    ///
    /// SND_ASYNC は呼び出しから戻った後も再生中バッファを読み続けるため、
    /// マネージド配列を渡すとGCの移動で壊れうる。呼び出し側が
    /// <see cref="WaveBuffer"/> で確保した領域を渡すこと。
    /// </summary>
    public static void Play(IntPtr waveData)
    {
        if (waveData != IntPtr.Zero)
            PlaySound(waveData, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    /// <summary>再生中も動かないようアンマネージド側に置いたWAVデータ。</summary>
    public sealed class WaveBuffer : IDisposable
    {
        public IntPtr Pointer { get; private set; }

        public WaveBuffer(byte[] wav)
        {
            Pointer = Marshal.AllocHGlobal(wav.Length);
            Marshal.Copy(wav, 0, Pointer, wav.Length);
        }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
                return;
            // 再生中に解放すると雑音になるため、先に停止させる
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }
}
