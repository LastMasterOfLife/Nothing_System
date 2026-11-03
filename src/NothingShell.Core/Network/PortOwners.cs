using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace NothingShell.Core.Network;

/// <summary>Chi ha una porta TCP: il processo in ascolto e quante connessioni ci sono.</summary>
public sealed record PortOwner(int Port, string Address, string State, int Pid, string Process, string? Remote);

/// <summary>
/// "Chi tiene la porta 5432?": legge la tabella TCP di Windows (<c>GetExtendedTcpTable</c>, senza PowerShell né permessi)
/// e la unisce ai nomi dei processi (docs/18-connettori-diagnostica.md).
/// </summary>
public static partial class PortOwners
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;

    public static IReadOnlyList<PortOwner> Find(int port)
    {
        var rows = new List<RawRow>();
        rows.AddRange(Read(AfInet));
        rows.AddRange(Read(AfInet6));
        var names = new Dictionary<int, string>();
        string NameOf(int pid)
        {
            if (!names.TryGetValue(pid, out var name))
            {
                try
                {
                    name = Process.GetProcessById(pid).ProcessName;
                }
                catch (ArgumentException)
                {
                    name = pid == 0 ? "Idle" : "terminato";
                }
                catch (InvalidOperationException)
                {
                    name = "terminato";
                }
                names[pid] = name;
            }
            return name;
        }
        return rows.Where(r => r.LocalPort == port)
            .OrderBy(r => r.State == "In ascolto" ? 0 : 1).ThenBy(r => r.RemoteAddress, StringComparer.Ordinal)
            .Select(r => new PortOwner(r.LocalPort, r.LocalAddress, r.State, r.Pid, NameOf(r.Pid), r.State == "In ascolto" ? null : $"{r.RemoteAddress}:{r.RemotePort}"))
            .ToList();
    }

    public static string StateName(uint state) => state switch
    {
        1 => "Chiusa", 2 => "In ascolto", 3 => "SYN inviato", 4 => "SYN ricevuto", 5 => "Connessa", 6 => "FIN-WAIT-1", 7 => "FIN-WAIT-2",
        8 => "CLOSE-WAIT", 9 => "In chiusura", 10 => "LAST-ACK", 11 => "TIME-WAIT", 12 => "Eliminata", _ => state.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private sealed record RawRow(string State, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, int Pid);

    private static List<RawRow> Read(int family)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0) != 0)
                return [];
            var count = Marshal.ReadInt32(buffer);
            var rows = new List<RawRow>(count);
            // v4: stato, indirizzo locale, porta locale, indirizzo remoto, porta remota, pid (6 × 4 byte)
            // v6: indirizzo (16) + ambito (4) + porta (4), poi lo stesso per il remoto, stato e pid
            var rowSize = family == AfInet ? 24 : 56;
            for (var i = 0; i < count; i++)
            {
                var p = buffer + 4 + (i * rowSize);
                if (family == AfInet)
                {
                    rows.Add(new RawRow(StateName((uint)Marshal.ReadInt32(p)), new IPAddress((uint)Marshal.ReadInt32(p, 4)).ToString(), Port(Marshal.ReadInt32(p, 8)),
                        new IPAddress((uint)Marshal.ReadInt32(p, 12)).ToString(), Port(Marshal.ReadInt32(p, 16)), Marshal.ReadInt32(p, 20)));
                }
                else
                {
                    var local = new byte[16];
                    var remote = new byte[16];
                    Marshal.Copy(p, local, 0, 16);
                    Marshal.Copy(p + 24, remote, 0, 16);
                    rows.Add(new RawRow(StateName((uint)Marshal.ReadInt32(p, 48)), new IPAddress(local).ToString(), Port(Marshal.ReadInt32(p, 20)),
                        new IPAddress(remote).ToString(), Port(Marshal.ReadInt32(p, 44)), Marshal.ReadInt32(p, 52)));
                }
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>La porta è nei due byte bassi, in ordine di rete.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
