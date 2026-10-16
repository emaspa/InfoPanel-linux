namespace InfoPanel.VmaxPanel;

internal interface IValkyriePanelDevice : IDisposable
{
    void SendFrame(byte[] pixels, int length);
    void SetScreenSwitch(bool enabled);
    void KeepAlive();
}
