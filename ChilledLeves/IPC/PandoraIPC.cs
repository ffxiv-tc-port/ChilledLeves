using ECommons.EzIpcManager;

namespace ChilledLeves.IPC;

#nullable disable
public class PandoraIPC
{
    public const string Name = "PandorasBox";
    // 這條仍是國際服的庫，因為台服艦隊沒有 PandorasBox 的移植版可指。
    // 目前零呼叫；要接安裝按鈕之前必須先確認目標庫是台服版，否則會裝進 API15 的外掛。
    public const string Repo = "https://love.puni.sh/ment.json";

    public PandoraIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);
    public bool Installed => HasPlugin(Name);

    [EzIPC] public readonly Func<string, bool?> GetFeatureEnabled;
    [EzIPC] public readonly Func<string, string, bool?> GetConfigEnabled;
    [EzIPC] public readonly Action<string, bool?> SetFeatureEnabled;
    [EzIPC] public readonly Action<string, string, bool> SetConfigEnabled;
    [EzIPC] public readonly Action<string, int> PauseFeature;
}
