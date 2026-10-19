using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Y700Switch2V60Viiper;

public static class NeonWindowChrome
{
    public static readonly DependencyProperty EnabledProperty=DependencyProperty.RegisterAttached(
        "Enabled",typeof(bool),typeof(NeonWindowChrome),new PropertyMetadata(false,OnEnabled));
    public static bool GetEnabled(DependencyObject value)=>(bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value,bool enabled)=>value.SetValue(EnabledProperty,enabled);
    private static void OnEnabled(DependencyObject target,DependencyPropertyChangedEventArgs e)
    {
        if(target is Window window && e.NewValue is true)window.SourceInitialized+=(_,_)=>Apply(window);
    }
    public static void Apply(Window window)
    {
        try
        {
            IntPtr handle=new WindowInteropHelper(window).Handle;
            int dark=1;
            if(DwmSetWindowAttribute(handle,20,ref dark,4)!=0)DwmSetWindowAttribute(handle,19,ref dark,4);
            int caption=0x170C08, text=0xFFEEE7, border=0x493D37;
            DwmSetWindowAttribute(handle,35,ref caption,4);
            DwmSetWindowAttribute(handle,36,ref text,4);
            DwmSetWindowAttribute(handle,34,ref border,4);
        }
        catch { /* Older Windows versions retain their native title bar. */ }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
}
