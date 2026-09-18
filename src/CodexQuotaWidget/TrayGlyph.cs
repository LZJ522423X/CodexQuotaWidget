using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexQuotaWidget;
internal static class TrayGlyph
{
    [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr icon);
    public static Icon Create(double? remaining,bool stale)
    {
        using var bitmap=new Bitmap(32,32);
        using var g=Graphics.FromImage(bitmap);g.SmoothingMode=SmoothingMode.AntiAlias;
        var color=stale?Color.FromArgb(161,170,184):remaining<=10?Color.FromArgb(255,106,112):remaining<=25?Color.FromArgb(243,200,85):Color.FromArgb(104,231,170);
        using var track=new Pen(Color.FromArgb(95,105,115),3);using var progress=new Pen(color,3);
        using var fill=new SolidBrush(Color.FromArgb(20,26,33));g.FillEllipse(fill,1,1,30,30);g.DrawEllipse(track,2,2,28,28);
        if(remaining is {} value && value>0)g.DrawArc(progress,2,2,28,28,-90,(float)(Math.Clamp(value,0,100)*3.6));
        using var font=new Font("Segoe UI",remaining>=100?8:10,FontStyle.Bold,GraphicsUnit.Pixel);
        using var ink=new SolidBrush(color);using var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center};
        g.DrawString(remaining?.ToString("0")??"?",font,ink,new RectangleF(1,1,30,30),format);
        var handle=bitmap.GetHicon();try{return (Icon)Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}
    }
}
