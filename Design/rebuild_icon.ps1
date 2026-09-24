Add-Type -AssemblyName System.Drawing
$code = @'
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

public static class IconMaker
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>从 PNG 源生成多尺寸标准 ICO（32bpp ARGB DIB 帧，WIC 兼容）</summary>
    public static byte[] BuildIco(string pngPath, int[] sizes)
    {
        byte[][] frames = new byte[sizes.Length][];
        using (var src = new Bitmap(pngPath))
        {
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                using (var bmp = new Bitmap(src, new Size(s, s)))
                using (var icon = Icon.FromHandle(bmp.GetHicon()))
                {
                    using (var ms = new MemoryStream())
                    {
                        icon.Save(ms);
                        frames[i] = new byte[ms.Length - 22];
                        Array.Copy(ms.ToArray(), 22, frames[i], 0, frames[i].Length);
                    }
                    DestroyIcon(icon.Handle);
                }
            }
        }
        int count = sizes.Length;
        int offset = 6 + 16 * count;
        using (var ms = new MemoryStream())
        {
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((ushort)0);   // reserved
                bw.Write((ushort)1);   // type = icon
                bw.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    int s = sizes[i];
                    bw.Write((byte)(s >= 256 ? 0 : s));
                    bw.Write((byte)(s >= 256 ? 0 : s));
                    bw.Write((byte)0); // palette
                    bw.Write((byte)0); // reserved
                    bw.Write((ushort)1);  // planes
                    bw.Write((ushort)32); // bpp
                    bw.Write((uint)frames[i].Length);
                    bw.Write((uint)offset);
                    offset += frames[i].Length;
                }
                for (int i = 0; i < count; i++) bw.Write(frames[i]);
            }
            return ms.ToArray();
        }
    }
}
'@
Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

$png = 'C:\Users\59538\Desktop\RBLAOI\Design\preview\5_scan_badge.png'
$ico = 'C:\Users\59538\Desktop\RBLAOI\RBLAOI.ico'
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$data = [IconMaker]::BuildIco($png, $sizes)
[IO.File]::WriteAllBytes($ico, $data)
'ICO rebuilt: ' + $data.Length + ' bytes, frames=' + $sizes.Count

# WIC 解码验证
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$fs = [IO.File]::OpenRead($ico)
$dec = New-Object System.Windows.Media.Imaging.IconBitmapDecoder($fs, [Windows.Media.Imaging.BitmapCreateOptions]::None, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
'DECODE OK, frames: ' + $dec.Frames.Count
foreach ($f in $dec.Frames) { '  frame ' + $f.PixelWidth + 'x' + $f.PixelHeight + ' fmt=' + $f.Format }
$fs.Close()
