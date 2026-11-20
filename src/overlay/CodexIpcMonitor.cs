using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexInputUsage
{
    internal sealed class IpcRouteStatus
    {
        internal string ActiveThreadId;
        internal int WindowCount;
        internal int DistinctThreadCount;
        internal bool IsAmbiguousMultiWindow;
        internal bool IsConnected;
        internal long Version;
        internal string LastError;
    }

    internal sealed class CodexIpcMonitor : IDisposable
    {
        private const int MaxWireFrameBytes = 256 * 1024 * 1024;
        private const int MaxJsonFrameBytes = 4 * 1024 * 1024;
        private const string SourceClientId = "codex-inputusage";

        private sealed class ActiveConv
        {
            internal string ThreadId;
            internal long Sequence;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, ActiveConv> _activeByWindow = new Dictionary<string, ActiveConv>(StringComparer.Ordinal);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = MaxJsonFrameBytes };
        private CancellationTokenSource _cts;
        private Task _runner;
        private string _activeThreadId;
        private int _distinctThreadCount;
        private bool _isAmbiguousMultiWindow;
        private string _lastError;
        private bool _isConnected;
        private long _sequence;
        private long _version;

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
                    ActiveThreadId = _activeThreadId,
                    WindowCount = _activeByWindow.Count,
                    DistinctThreadCount = _distinctThreadCount,
                    IsAmbiguousMultiWindow = _isAmbiguousMultiWindow,
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
            string key = (string.IsNullOrWhiteSpace(sourceClient) ? "unattributed" : sourceClient) + "\u001f" + hostId;
            lock (_sync)
            {
                _isConnected = true;
                ApplyFollowingLocked(key, convId, following);
            }
        }

        internal void SimulateDisconnectForTest()
        {
            MarkDisconnected("simulated-disconnect");
        }

        private async Task RunAsync(CancellationToken token)
        {
            int retryMs = 350;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        await ConnectWithCancelAsync(pipe, 2500, token).ConfigureAwait(false);
                        MarkConnected();
                        retryMs = 350;
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
                retryMs = Math.Min(5000, retryMs * 2);
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
                        string prefix = clientId + "\u001f";
                        lock (_sync)
                        {
                            var toRemove = _activeByWindow.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                            for (int i = 0; i < toRemove.Length; i++) _activeByWindow.Remove(toRemove[i]);
                            RecomputeActiveThreadLocked();
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
            string key = (string.IsNullOrWhiteSpace(sourceClient) ? "unattributed" : sourceClient) + "\u001f" + hostId;

            lock (_sync)
            {
                ApplyFollowingLocked(key, convId, following);
            }
        }

        private void ApplyFollowingLocked(string key, string convId, bool following)
        {
            if (following)
            {
                _activeByWindow[key] = new ActiveConv { ThreadId = convId, Sequence = ++_sequence };
            }
            else
            {
                ActiveConv cur;
                if (_activeByWindow.TryGetValue(key, out cur)
                    && string.Equals(cur.ThreadId, convId, StringComparison.OrdinalIgnoreCase))
                {
                    _activeByWindow.Remove(key);
                }
            }
            RecomputeActiveThreadLocked();
        }

        private void MarkConnected()
        {
            lock (_sync)
            {
                _isConnected = true;
                _lastError = null;
                _version++;
            }
        }

        private void MarkDisconnected(string error)
        {
            lock (_sync)
            {
                _isConnected = false;
                if (!string.IsNullOrWhiteSpace(error)) _lastError = error;
                _activeByWindow.Clear();
                _activeThreadId = null;
                _distinctThreadCount = 0;
                _isAmbiguousMultiWindow = false;
                _version++;
            }
        }

        private void RecomputeActiveThreadLocked()
        {
            // Strict no-guess policy: if multiple Codex windows are simultaneously following
            // different conversations, IPC does not carry the Win32 HWND, so refuse to guess
            // and clear the active thread until unambiguous.
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string singleThread = null;
            foreach (var kv in _activeByWindow)
            {
                if (!string.IsNullOrWhiteSpace(kv.Value.ThreadId))
                {
                    distinct.Add(kv.Value.ThreadId);
                    singleThread = kv.Value.ThreadId;
                }
            }

            _distinctThreadCount = distinct.Count;
            bool ambiguous = distinct.Count > 1;
            string next = distinct.Count == 1 ? singleThread : null;

            if (!string.Equals(_activeThreadId, next, StringComparison.OrdinalIgnoreCase)
                || _isAmbiguousMultiWindow != ambiguous)
            {
                _activeThreadId = next;
                _isAmbiguousMultiWindow = ambiguous;
                _version++;
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
