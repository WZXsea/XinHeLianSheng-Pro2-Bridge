namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    public bool SwapABButtons
    {
        get => userSettings.SwapABButtons;
        set
        {
            if (userSettings.SwapABButtons == value) return;
            userSettings.SwapABButtons = value;
            ApplyFaceButtonMapping();
            OnPropertyChanged();
        }
    }

    public bool SwapXYButtons
    {
        get => userSettings.SwapXYButtons;
        set
        {
            if (userSettings.SwapXYButtons == value) return;
            userSettings.SwapXYButtons = value;
            ApplyFaceButtonMapping();
            OnPropertyChanged();
        }
    }

    public string FaceButtonMappingSummary => "以 Xbox 输出为例：手柄 " +
        (SwapABButtons ? "A → A，B → B" : "A → B，B → A") + "；" +
        (SwapXYButtons ? "X → X，Y → Y" : "X → Y，Y → X") + "。";

    private FaceButtonMapping CurrentFaceButtonMapping => new(SwapABButtons, SwapXYButtons, userSettings.ButtonRemapping);

    private void ApplyFaceButtonMapping()
    {
        var mapping = CurrentFaceButtonMapping;
        session?.SetFaceButtonMapping(mapping);
        foreach (var slot in pro2Slots) slot.Session?.SetFaceButtonMapping(mapping);
        SaveUserSettings("[FACE_BUTTON_MAPPING]");
        AppendLog("[FACE_BUTTON_MAPPING] swap_ab=" + mapping.SwapAB + " swap_xy=" + mapping.SwapXY +
            " applies=all_output_modes transport_reconnect=false");
        OnPropertyChanged(nameof(FaceButtonMappingSummary));
    }
}
