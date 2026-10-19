using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public delegate bool RumblePacketWriter(byte[] report,out string error);

// One short audition worker. Game feedback is retained during audition and
// restored afterwards; restarting a preview never lets an older stop mute it.
public sealed class RumblePreviewMixer(Func<bool> available,RumblePacketWriter write)
{
    private readonly object gate=new();
    private readonly byte[] preview=Pro2OutputPacketMapper.BuildOrdinaryPacket(40,96,"slider-preview").Report;
    private readonly byte[] neutral=Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"preview-stop").Report;
    private byte[]? game;
    private CancellationTokenSource? cancellation;
    private Task worker=Task.CompletedTask;
    private bool active,restoreGame=true;
    private long deadline,hardDeadline;
    public bool IsActive { get { lock(gate)return active; } }

    public bool WriteGame(ReadOnlySpan<byte> report,out string error)
    {
        lock(gate)
        {
            if(report.Length!=64 || report[0]!=2) { error="无效的振动报告";return false; }
            if(!available()) { error="手柄振动输出未就绪";return false; }
            game ??=new byte[64];report.CopyTo(game);
            if(active) { error="";return true; }
            return write(game,out error);
        }
    }
    public bool Request(int durationMs,out string error)
    {
        lock(gate)
        {
            if(!available()) { error="请先连接手柄";return false; }
            long now=Stopwatch.GetTimestamp();
            if(!active)
            {
                // A cancelled worker must drain its stop before any new one.
                if(!worker.IsCompleted) { error="振动预览正在停止";return false; }
                if(!write(preview,out error))return false;
                active=true;restoreGame=true;
                hardDeadline=now+2*Stopwatch.Frequency;
                deadline=now+Math.Clamp(durationMs,80,500)*Stopwatch.Frequency/1000;
                cancellation?.Dispose();cancellation=new CancellationTokenSource();
                CancellationToken token=cancellation.Token;
                worker=Task.Run(()=>RunAsync(token));
            }
            else deadline=Math.Min(hardDeadline,now+Math.Clamp(durationMs,80,500)*Stopwatch.Frequency/1000);
            error="";return true;
        }
    }
    public void FinishGesture()
    {
        lock(gate) if(active)deadline=Math.Min(deadline,Stopwatch.GetTimestamp()+100*Stopwatch.Frequency/1000);
    }
    public async Task StopAsync(bool resumeGame=true)
    {
        Task pending;
        lock(gate) { restoreGame=resumeGame;deadline=0;cancellation?.Cancel();pending=worker; }
        await pending.ConfigureAwait(false);
        lock(gate)if(!resumeGame)game=null;
    }
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while(true)
            {
                await Task.Delay(16,token).ConfigureAwait(false);
                lock(gate)
                {
                    if(Stopwatch.GetTimestamp()>=deadline || !available())break;
                    if(!write(preview,out _))break;
                }
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        finally
        {
            lock(gate)
            {
                try
                {
                    if(available())
                    {
                        write(neutral,out _);
                        if(restoreGame && game!=null)write(game,out _);
                    }
                }
                finally { active=false; }
            }
        }
    }
}
