namespace DynoCommandPC.Transport;

/// <summary>A byte-stream link to the dyno over classic Bluetooth RFCOMM/SPP.</summary>
public interface ITransport : IDisposable
{
    bool IsOpen { get; }
    string Description { get; }
    bool Open();
    void Close();
    void Write(byte[] data, int length);
    event Action<byte[]>? DataReceived;
}
