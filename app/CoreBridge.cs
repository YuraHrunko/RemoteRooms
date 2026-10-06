using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteRooms;

public sealed class Room
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Width { get; set; } = 900;
    public double Height { get; set; } = 600;
}
public sealed class Desk
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 120;
    public double Height { get; set; } = 64;
}
public sealed class Computer
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}
public sealed class Endpoint
{
    public string Id { get; set; } = "";
    public string ComputerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
}
public sealed class WorkspaceData
{
    public int Version { get; set; }
    public List<Room> Rooms { get; set; } = [];
    public List<Desk> Desks { get; set; } = [];
    public List<Computer> Computers { get; set; } = [];
    public List<Endpoint> Endpoints { get; set; } = [];
}

internal static class CoreBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    [DllImport("RemoteRooms.Core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int rr_open(string path);
    [DllImport("RemoteRooms.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rr_state();
    [DllImport("RemoteRooms.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rr_error();
    [DllImport("RemoteRooms.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rr_apply([MarshalAs(UnmanagedType.LPUTF8Str)] string command);
    [DllImport("RemoteRooms.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rr_undo();
    [DllImport("RemoteRooms.Core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rr_redo();

    public static void Open(string path)
    {
        if (rr_open(path) == 0) throw new InvalidOperationException(Error());
    }
    public static WorkspaceData State() => JsonSerializer.Deserialize<WorkspaceData>(Marshal.PtrToStringUTF8(rr_state())!, JsonOptions)!;
    public static string StateJson() => Marshal.PtrToStringUTF8(rr_state())!;
    public static void Apply(object command)
    {
        if (rr_apply(JsonSerializer.Serialize(command, JsonOptions)) == 0) throw new InvalidOperationException(Error());
    }
    public static bool Undo() => rr_undo() == 1;
    public static bool Redo() => rr_redo() == 1;
    public static string Error() => Marshal.PtrToStringUTF8(rr_error()) ?? "Невідома помилка";
}
