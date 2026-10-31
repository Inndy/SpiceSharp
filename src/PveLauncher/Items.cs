using System.Globalization;
using Pve.Api;

namespace PveLauncher;

/// <summary>A saved server as the list shows it.</summary>
public sealed partial class ServerItem(PveServerConfig config)
{
    public PveServerConfig Config { get; } = config;
    public string Name => Config.DisplayName;
    public string Detail
    {
        get
        {
            string where;
            try { where = PveClient.NormalizeApiUrl(Config.Url).Authority; }
            catch (PveApiException) { where = Config.Url; }
            return $"{Config.Account} · {where}{(string.IsNullOrWhiteSpace(Config.CaPem) ? "" : " · pinned certificate")}";
        }
    }
}

/// <summary>A VM or container as the list shows it.</summary>
public sealed partial class GuestItem(PveVm vm)
{
    public PveVm Vm { get; } = vm;
    public string Vmid => Vm.Vmid.ToString(CultureInfo.InvariantCulture);
    public string Name => Vm.Name;
    public string Node => Vm.Node;
    public string Type => Vm.IsTemplate ? Vm.TypeText + " (template)" : Vm.TypeText;
    public string Status => Vm.Status;
    public double Opacity => Vm.IsRunning ? 1.0 : 0.6;
    public string Label => $"{(Vm.Type == PveGuestType.Lxc ? "CT" : "VM")} {Vm.Vmid}{(Vm.Name.Length > 0 ? " (" + Vm.Name + ")" : "")}";

    public bool Matches(string filter) => filter.Length == 0
        || Vm.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Vmid.Contains(filter, StringComparison.Ordinal)
        || Vm.Node.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
