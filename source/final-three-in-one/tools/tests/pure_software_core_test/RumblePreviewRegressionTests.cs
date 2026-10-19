using System.Collections.Concurrent;
using Y700Switch2V60Viiper;

internal static class RumblePreviewRegressionTests
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        bool available=false;
        var packets=new ConcurrentQueue<byte[]>();
        bool Write(byte[] packet,out string error) { packets.Enqueue(packet.ToArray());error="";return true; }
        var mixer=new RumblePreviewMixer(()=>available,Write);
        check(!mixer.Request(100,out _) && packets.IsEmpty,"offline preview neither opens nor drives a controller");
        available=true;
        byte[] firstGame=Pro2OutputPacketMapper.BuildOrdinaryPacket(20,80,"fixture-game").Report;
        byte[] lastGame=Pro2OutputPacketMapper.BuildOrdinaryPacket(30,100,"fixture-game").Report;
        check(mixer.WriteGame(firstGame,out _) && packets.Count==1,"ordinary feedback remains unchanged outside audition");
        check(mixer.Request(100,out _) && mixer.IsActive,"user request immediately starts preview");
        int before=packets.Count;
        check(mixer.WriteGame(lastGame,out _) && packets.Count==before,"game feedback is retained while preview owns output");
        await Task.Delay(30);
        check(mixer.Request(180,out _),"rapid slider motion updates the existing preview worker");
        await Task.Delay(100);
        check(mixer.IsActive,"an older pulse deadline cannot prematurely stop a newer slider update");
        await mixer.StopAsync();
        check(!mixer.IsActive && packets.Last().SequenceEqual(lastGame),"preview stops and restores the latest game feedback");
        packets.Clear();
        var isolated=new RumblePreviewMixer(()=>available,Write);
        isolated.Request(80,out _);await Task.Delay(150);
        check(!isolated.IsActive && packets.Last().SequenceEqual(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"neutral").Report),
            "short test stops automatically without a game or another UI event");
        isolated.Request(500,out _);await isolated.StopAsync(false);
        check(!isolated.IsActive,"disconnect/shutdown drains the preview worker");
        available=false;
        check(!isolated.Request(100,out _),"lost transport cannot silently claim preview success");

        ulong Sum(byte[] packet)
        {
            ulong value=0;for(int i=0;i<5;i++)value|=(ulong)packet[2+i]<<(i*8);
            return ((value>>10)&1023)+((value>>30)&1023);
        }
        var raw=Pro2OutputPacketMapper.BuildOrdinaryPacket(255,255,"full-feedback").Report;
        ulong previous=0;
        foreach(double gain in new[]{0.0,.25,.5,1.0})
        {
            check(Pro2UsbProtocol.TryEncodeRumble(raw,0,gain,out var encoded,out _),"USB gain encodes "+gain);
            ulong amplitude=Sum(encoded);
            check(gain==0 ? amplitude==0 : amplitude>previous && amplitude<=511,
                "USB low/medium/full strengths remain distinct "+gain);
            previous=amplitude;
        }
        raw=Pro2OutputPacketMapper.BuildOrdinaryPacket(40,96,"preview").Report;previous=0;
        foreach(double gain in new[]{.1,.5,1,2,3})
        {
            Pro2UsbProtocol.TryEncodeRumble(raw,0,gain,out var encoded,out _);
            ulong amplitude=Sum(encoded);
            check(amplitude>previous && amplitude<=511,"preview stays graded through 300 percent: "+gain);
            previous=amplitude;
        }
    }
}
