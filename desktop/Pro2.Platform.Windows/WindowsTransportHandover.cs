using Y700Switch2V60Viiper;

namespace Pro2.Platform.Windows;

internal static class WindowsTransportHandover
{
    public static Pro2InputTransport Preference(string value)=>value switch
    {
        "auto"=>Pro2InputTransport.Auto,"usb"=>Pro2InputTransport.Usb,"bluetooth"=>Pro2InputTransport.Bluetooth,
        _=>throw new HostFailure("invalid_settings","inputTransport 取值为 auto、usb 或 bluetooth。")
    };
    public static string PreferenceKey(Pro2InputTransport value)=>value switch
    {
        Pro2InputTransport.Usb=>"usb",Pro2InputTransport.Bluetooth=>"bluetooth",_=>"auto"
    };
    public static bool Eligible(Pro2InputTransport preference,bool alreadyUsb,bool connected,int participants,int freeUsb,bool busy)=>
        Pro2TransportSwitchPolicy.CanAutoSwitch(preference,alreadyUsb,connected,participants,freeUsb,busy);
    public static string ActualTransport(bool connected,bool usb)=>connected?(usb?"usb":"bluetooth"):"disconnected";
    public static void SelfTest()
    {
        foreach(var preference in new[]{Pro2InputTransport.Auto,Pro2InputTransport.Usb,Pro2InputTransport.Bluetooth})
        foreach(bool alreadyUsb in new[]{false,true})
        foreach(bool connected in new[]{false,true})
        foreach(int participants in new[]{0,1,2})
        foreach(int freeUsb in new[]{0,1,2})
        foreach(bool busy in new[]{false,true})
        {
            bool expected=preference==Pro2InputTransport.Auto && !alreadyUsb && connected && participants==1 && freeUsb==1 && !busy;
            if(Eligible(preference,alreadyUsb,connected,participants,freeUsb,busy)!=expected)
                throw new HostFailure("self_test_failed","原 USB 热插接管的单手柄、单候选及连接偏好策略校验失败。");
        }
        if(ActualTransport(true,true)!="usb" || ActualTransport(true,false)!="bluetooth" || ActualTransport(false,true)!="disconnected")
            throw new HostFailure("self_test_failed","实体接管后的真实连接状态分类校验失败。");
        foreach(string key in new[]{"auto","usb","bluetooth"})
            if(PreferenceKey(Preference(key))!=key)throw new HostFailure("self_test_failed","连接偏好协议映射校验失败。");
    }
}
