using System.Net.Sockets;
using System.Net.WebSockets;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Shared;

/// <summary>
/// Runs a network call with a short timeout and turns the usual failures into a
/// <see cref="PluginException"/> a person understands ("The lamp didn't answer within 3 s.").
/// </summary>
/// <remarks>
/// Without this, our own timeout surfaces as an <see cref="OperationCanceledException"/>, which the
/// hub reports as "Cancelled." — confusing when someone just asked for the lamp.
/// </remarks>
internal static class DeviceCall
{
    public static async Task<T> RunAsync<T>(string what, TimeSpan timeout, CancellationToken cancel, Func<CancellationToken, Task<T>> call)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try
        {
            return await call(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new PluginException($"{Text.Capitalize(what)} didn't answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e)
        {
            throw new PluginException($"Couldn't reach {what} ({Reason(e)}).", e);
        }
        catch (SocketException e)
        {
            throw new PluginException($"Couldn't reach {what} ({e.SocketErrorCode}).", e);
        }
        catch (WebSocketException e)
        {
            throw new PluginException($"Couldn't talk to {what} ({e.Message.TrimEnd('.')}).", e);
        }
        catch (IOException e)
        {
            throw new PluginException($"Lost the connection to {what} ({e.Message.TrimEnd('.')}).", e);
        }
    }

    public static Task RunAsync(string what, TimeSpan timeout, CancellationToken cancel, Func<CancellationToken, Task> call) =>
        RunAsync<bool>(what, timeout, cancel, async c =>
        {
            await call(c).ConfigureAwait(false);
            return true;
        });

    static string Reason(HttpRequestException e) =>
        e.InnerException is SocketException se ? se.SocketErrorCode.ToString() : e.Message.TrimEnd('.');
}
