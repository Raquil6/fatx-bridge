namespace FatxBridge.Windows;

public interface IFatxMountSession : IDisposable
{
    string MountPath { get; }
    void OpenExplorer();
}
