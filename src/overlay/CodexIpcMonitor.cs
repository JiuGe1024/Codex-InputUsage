using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexInputUsage
{
    internal sealed class IpcRouteStatus
    {
        internal string[] FollowedThreadIds = new string[0];
        internal string LastFollowedThreadId;
        internal bool IsConnected;
        internal long Version;
        internal string LastError;
    }

    internal sealed class CodexIpcMonitor : IDisposable
    {
        private const int MaxWireFrameBytes = 256 * 1024 * 1024;
        private const int MaxJsonFrameBytes = 4 * 1024 * 1024;
        private const string SourceClientId = "codex-inputusage";
        private const string PipePath = @"\\.\pipe\codex-ipc";

        [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WaitNamedPipe(string name, uint timeoutMs);

        private sealed class FollowedEntry
        {
            internal string SourceClient;
            internal string HostId;
            internal string ThreadId;
            internal long Sequence;
        }

        private readonly object _sync = new object();
        // Keyed by (sourceClient, hostId, conversationId) so multiple warm followed threads
        // from the same Codex window never silently overwrite each other.
        private readonly Dictionary<string, FollowedEntry> _followed = new Dictionary<string, FollowedEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = MaxJsonFrameBytes };
        private CancellationTokenSource _cts;
        private Task _runner;
        private string[] _cachedThreadIds = new string[0];
        private string _lastFollowedThreadId;
        private string _lastError;
        private bool _isConnected;
        private long _sequence;
        private long _version;

        internal event EventHandler RouteUpdated;

        internal bool IsRunning
        {
            get
            {
                lock (_sync) { return _runner != null; }
            }
        }

        internal void Start()
        {
            lock (_sync)
            {
                if (_runner != null) return;
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;
                _runner = Task.Run(delegate { RunAsync(token).Wait(); });
            }
        }

        internal void Stop()
        {
            CancellationTokenSource cts;
            lock (_sync)
            {
                cts = _cts;
                _cts = null;
                _runner = null;
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
            MarkDisconnected(null);
        }

        internal IpcRouteStatus GetStatus()
        {
            lock (_sync)
            {
                return new IpcRouteStatus
                {
                    FollowedThreadIds = (string[])_cachedThreadIds.Clone(),
                    LastFollowedThreadId = _lastFollowedThreadId,
                    IsConnected = _isConnected,
                    Version = _version,
                    LastError = _lastError
                };
            }
        }

        public void Dispose()
        {
            Stop();
        }

        internal void ApplyFollowingEventForTest(string sourceClient, string hostId, string convId, bool following)
        {
            lock (_sync)
            {
                _isConnected = true;
                ApplyFollowingLocked(sourceClient, hostId, convId, following);
            }
        }

        internal void SimulateDisconnectForTest()
        {
            MarkDisconnected("simulated-disconnect");
        }

        private async Task RunAsync(CancellationToken token)
        {
            int retryMs = 500;
            while (!token.IsCancellationRequested)
            {
                bool pipeReady = false;
                try { pipeReady = WaitNamedPipe(PipePath, 1); } catch { }

                if (!pipeReady)
                {
                    MarkDisconnected("pipe-not-found");
                    try { await Task.Delay(retryMs, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    retryMs = Math.Min(15000, retryMs * 2);
                    continue;
                }

                try
                {
                    using (var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        await ConnectWithCancelAsync(pipe, 1500, token).ConfigureAwait(false);
                        MarkConnected();
                        retryMs = 500;
                        await SendInitializeAsync(pipe, token).ConfigureAwait(false);

                        byte[] prefix = new byte[4];
                        while (!token.IsCancellationRequested && pipe.IsConnected)
                        {
                            if (!await ReadExactlyAsync(pipe, prefix, 4, token).ConfigureAwait(false)) break;
                            uint frameLen = BitConverter.ToUInt32(prefix, 0);
                            if (frameLen == 0 || frameLen > MaxWireFrameBytes) break;
                            if (frameLen > MaxJsonFrameBytes)
                            {
                                if (!await DrainExactlyAsync(pipe, frameLen, token).ConfigureAwait(false)) break;
                                continue;
                            }
                            byte[] payload = new byte[(int)frameLen];
                            if (!await ReadExactlyAsync(pipe, payload, (int)frameLen, token).ConfigureAwait(false)) break;
                            try { ProcessFrame(payload); } catch { }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MarkDisconnected(ex.Message);
                }

                MarkDisconnected(null);
                try { await Task.Delay(retryMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retryMs = Math.Min(15000, retryMs * 2);
            }
        }

        private static Task ConnectWithCancelAsync(NamedPipeClientStream pipe, int timeoutMs, CancellationToken token)
        {
            return Task.Run(delegate
            {
                token.ThrowIfCancellationRequested();
                pipe.Connect(timeoutMs);
            }, token);
        }

        private static async Task SendInitializeAsync(Stream pipe, CancellationToken token)
        {
            string req = string.Format(
                "{{\"type\":\"request\",\"requestId\":\"{0}\",\"sourceClientId\":\"{1}\",\"version\":0,\"method\":\"initialize\",\"params\":{{\"clientType\":\"{1}\"}}}}",
                Guid.NewGuid().ToString(), SourceClientId);
            byte[] payload = Encoding.UTF8.GetBytes(req);
            byte[] prefix = BitConverter.GetBytes((uint)payload.Length);
            await pipe.WriteAsync(prefix, 0, prefix.Length, token).ConfigureAwait(false);
            await pipe.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
            await pipe.FlushAsync(token).ConfigureAwait(false);
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = await stream.ReadAsync(buffer, offset, count - offset, token).ConfigureAwait(false);
                if (n <= 0) return false;
                offset += n;
            }
            return true;
        }

        private static async Task<bool> DrainExactlyAsync(Stream stream, uint count, CancellationToken token)
        {
            byte[] scratch = new byte[8192];
            uint remaining = count;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min((uint)scratch.Length, remaining);
                int n = await stream.ReadAsync(scratch, 0, chunk, token).ConfigureAwait(false);
                if (n <= 0) return false;
                remaining -= (uint)n;
            }
            return true;
        }

        private void ProcessFrame(byte[] payload)
        {
            string text = Encoding.UTF8.GetString(payload);
            if (text.IndexOf("\"broadcast\"", StringComparison.Ordinal) < 0) return;

            var root = _json.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null) return;
            if (!string.Equals(GetString(root, "type"), "broadcast", StringComparison.Ordinal)) return;

            string method = GetString(root, "method");
            var parameters = GetDict(root, "params");
            if (parameters == null) return;

            if (string.Equals(method, "client-status-changed", StringComparison.Ordinal))
            {
                if (string.Equals(GetString(parameters, "status"), "disconnected", StringComparison.Ordinal))
                {
                    string clientId = GetString(parameters, "clientId");
                    if (!string.IsNullOrWhiteSpace(clientId))
                    {
                        lock (_sync)
                        {
                            var toRemove = _followed
                                .Where(kv => string.Equals(kv.Value.SourceClient, clientId, StringComparison.OrdinalIgnoreCase))
                                .Select(kv => kv.Key)
                                .ToArray();
                            for (int i = 0; i < toRemove.Length; i++) _followed.Remove(toRemove[i]);
                            RebuildSnapshotLocked();
                        }
                    }
                }
                return;
            }

            if (!string.Equals(method, "thread-stream-following-changed", StringComparison.Ordinal)) return;
            string convId = GetString(parameters, "conversationId");
            string hostId = GetString(parameters, "hostId");
            object followingObj;
            if (string.IsNullOrWhiteSpace(convId) || string.IsNullOrWhiteSpace(hostId)
                || !parameters.TryGetValue("following", out followingObj) || !(followingObj is bool))
                return;

            bool following = (bool)followingObj;
            string sourceClient = GetString(root, "sourceClientId");

            lock (_sync)
            {
                ApplyFollowingLocked(sourceClient, hostId, convId, following);
            }
        }

        private void ApplyFollowingLocked(string sourceClient, string hostId, string convId, bool following)
        {
            string normClient = string.IsNullOrWhiteSpace(sourceClient) ? "unattributed" : sourceClient;
            string key = normClient + "\u001f" + hostId + "\u001f" + convId;
            if (following)
            {
                _followed[key] = new FollowedEntry
                {
                    SourceClient = normClient,
                    HostId = hostId,
                    ThreadId = convId,
                    Sequence = ++_sequence
                };
            }
            else
            {
                _followed.Remove(key);
            }
            RebuildSnapshotLocked();
        }

        private void MarkConnected()
        {
            lock (_sync)
            {
                _isConnected = true;
                _lastError = null;
                _version++;
            }
            NotifyUpdated();
        }

        private void MarkDisconnected(string error)
        {
            bool changed;
            lock (_sync)
            {
                changed = _isConnected || _followed.Count > 0 || _cachedThreadIds.Length > 0;
                _isConnected = false;
                if (!string.IsNullOrWhiteSpace(error)) _lastError = error;
                _followed.Clear();
                _cachedThreadIds = new string[0];
                _lastFollowedThreadId = null;
                if (changed) _version++;
            }
            if (changed) NotifyUpdated();
        }

        private void RebuildSnapshotLocked()
        {
            var ordered = _followed.Values
                .OrderByDescending(x => x.Sequence)
                .ToList();
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ordered.Count; i++)
            {
                if (seen.Add(ordered[i].ThreadId))
                    unique.Add(ordered[i].ThreadId);
            }
            _cachedThreadIds = unique.ToArray();
            _lastFollowedThreadId = unique.Count > 0 ? unique[0] : null;
            _version++;
            NotifyUpdated();
        }

        private void NotifyUpdated()
        {
            var handler = RouteUpdated;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object v;
            return dict != null && dict.TryGetValue(key, out v) ? v as string : null;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            object v;
            return dict != null && dict.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }
    }
}
