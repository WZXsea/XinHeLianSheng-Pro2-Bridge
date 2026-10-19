using System.Runtime.InteropServices;
namespace Pro2.Desktop.ViewModels;
public sealed partial class MainViewModel {
 public bool IsWindows=>OperatingSystem.IsWindows();
 public int SelectedXInputIndex{get=>Service.ActualOutputIndex;set{Service.ActualOutputIndex=Math.Clamp(value,0,3);Changed();}}
 public string ActualOutputSummary=>Current.ActualOutputAvailable?$"Windows XInput {SelectedXInputIndex} 实际读回 · {Current.ActualOutput.Buttons} · L {Current.ActualOutput.Lx},{Current.ActualOutput.Ly} · R {Current.ActualOutput.Rx},{Current.ActualOutput.Ry}":$"Windows XInput {SelectedXInputIndex} · 等待系统实例";
}
