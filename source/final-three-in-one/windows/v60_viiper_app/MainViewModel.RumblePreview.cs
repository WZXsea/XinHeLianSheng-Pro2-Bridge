using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private ICommand? previewRumbleCommand;
    private string rumblePreviewMessage="";
    public bool CanPreviewRumble => !previewOnly && !Busy && inputSource.IsOutputReady;
    public string RumblePreviewHint => rumblePreviewMessage.Length>0 ? rumblePreviewMessage :
        !inputSource.IsOutputReady ? "连接手柄后，拖动滑块即可短振预览" :
        RumblePercent<=0 ? "0% · 振动已关闭" : "拖动滑块即时短振，松开后自动停止";
    public ICommand PreviewRumbleCommand => previewRumbleCommand ??=new RelayCommand(_=>RequestRumblePreview(400),_=>CanPreviewRumble);
    public void RequestRumblePreview(int durationMs=180)
    {
        if(!CanPreviewRumble)return;
        if(inputSource.PreviewRumble(durationMs,out string error))
        {
            rumblePreviewMessage="";
            AppendLog("[RUMBLE_PREVIEW] requested_ms="+durationMs+" gain="+RumbleMultiplier.ToString("F2")+" transport="+inputSource.TransportLabel);
        }
        else rumblePreviewMessage=error;
        OnPropertyChanged(nameof(RumblePreviewHint));
    }
    public void FinishRumblePreviewGesture()=>inputSource.FinishRumblePreviewGesture();
    public Task StopRumblePreviewAsync()=>inputSource.StopRumblePreviewAsync();
}
