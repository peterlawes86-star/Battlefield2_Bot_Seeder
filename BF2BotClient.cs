using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BF2BotManager
{
    public enum ConnectionState
    {
        Disconnected,
        Reconnecting,
        TcpLogin,
        Connecting,
        Handshake,
        Connected,
        CdKeyAuth,
        Error
    }

    public class BF2BotClient
    {
        public ClientConfig Credentials { get; }
        public ServerConfig Server { get; }

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool ManuallyStopped { get; private set; } = false;
        public string ProfileId { get; private set; } = "0";
        public string UserId { get; private set; } = "0";
        public string SessKey { get; private set; } = "0";
        public string LoginToken { get; private set; } = string.Empty;

        public event Action<string, string>? OnLogMessage;
        public event Action<string, ConnectionState>? OnStateChanged;

        private CancellationTokenSource? _cts;
        private TaskCompletionSource<bool>? _connectTcs;
        private TcpClient? _tcpClient;
        private UdpClient? _udpClient;

        private uint _profileIdNum = 0;
        private uint _userIdNum = 0;
        private byte _packetTypePrefix = 0;
        private uint _totalBytesReceived = 0;

        private string _serverChallenge = string.Empty;
        private string _masterChallenge = string.Empty;
        private string _strCDKey = string.Empty;
        private string _strCDKeyHash = string.Empty;

        private static readonly byte[] ConnectionPkt = new byte[]
        {
            0x11, 0x20, 0x00, 0x01, 0x00, 0x00, 0x10, 0xC5, 0x50, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA0, 0xED, 0x8D,
            0x6C, 0xEE, 0x45, 0xCC, 0x4C, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        public BF2BotClient(ClientConfig credentials, ServerConfig server)
        {
            Credentials = credentials;
            Server = server;
        }

        public Task<bool> WaitForConnectedAsync()
        {
            if (State == ConnectionState.Connected) return Task.FromResult(true);
            return _connectTcs?.Task ?? Task.FromResult(false);
        }

        public async Task StartAsync()
        {
            if (State != ConnectionState.Disconnected && State != ConnectionState.Error) return;

            ManuallyStopped = false;
            _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _cts = new CancellationTokenSource();
            Log("Initiating full connection sequence...");
            PacketLogger.LogBotEvent(Credentials.Nickname, $"StartAsync initiated for server {Server.Address}:{Server.Port}");

            try
            {
                // Step 0: NAT Knock / Wakeup Packet to Master Server 27900
                await SendNatNegotiationAsync(_cts.Token);

                // Step 1: TCP Login to Master Server
                SetState(ConnectionState.TcpLogin);
                bool tcpOk = await TCPLoginAsync(_cts.Token);
                if (!tcpOk)
                {
                    Log("TCP Login to Master Server failed. Aborting.");
                    SetState(ConnectionState.Error);
                    return;
                }

                // Step 2: GPCM CDKey Authentication
                SetState(ConnectionState.CdKeyAuth);
                bool cdKeyOk = await DoCDKeyAuthAsync(_cts.Token);
                if (!cdKeyOk)
                {
                    Log("CDKey Auth failed. Aborting.");
                    SetState(ConnectionState.Error);
                    return;
                }

                // Step 3: Game Server Handshake & P7/P10 Auth
                SetState(ConnectionState.Connecting);
                bool gameOk = await GameConnectAsync(_cts.Token);
                if (!gameOk)
                {
                    Log("Game Server handshake failed. Aborting.");
                    SetState(ConnectionState.Error);
                    return;
                }

                // Step 4: Active Game Loop (KeepAlive & ACKs)
                await GameLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("Connection stopped by user.");
                SetState(ConnectionState.Disconnected);
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                PacketLogger.LogException(Credentials.Nickname, "StartAsync", ex);
                SetState(ConnectionState.Error);
            }
            finally
            {
                _connectTcs?.TrySetResult(State == ConnectionState.Connected);
            }
        }

        public void Stop()
        {
            ManuallyStopped = true;
            _cts?.Cancel();
            CleanupSockets();
            SetState(ConnectionState.Disconnected);
            _connectTcs?.TrySetResult(false);
            Log("Bot stopped.");
            PacketLogger.LogBotEvent(Credentials.Nickname, "Bot stopped and sockets closed.");
        }

        // ============================================================================
        // 1. TCP Master Server Login
        // ============================================================================
        private async Task<bool> TCPLoginAsync(CancellationToken token)
        {
            string masterHost = string.IsNullOrWhiteSpace(Server.LoginServer) ? "5.252.33.100" : Server.LoginServer;
            try
            {
                _tcpClient = new TcpClient();
                Log($"Connecting TCP to Master Server {masterHost}:29900...");
                PacketLogger.LogBotEvent(Credentials.Nickname, $"Connecting TCP to {masterHost}:29900");

                var connectTask = _tcpClient.ConnectAsync(masterHost, 29900, token).AsTask();
                var timeoutTask = Task.Delay(10000, token);

                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    Log("TCP Connect timed out (10s limit).");
                    return false;
                }

                await connectTask;
                NetworkStream stream = _tcpClient.GetStream();

                byte[] buffer = new byte[2048];
                int len = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (len <= 0) return false;

                PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", "TCP GPCM", $"{masterHost}:29900", buffer, len);
                string resp = Encoding.ASCII.GetString(buffer, 0, len);
                Log($"Received Master Challenge: {resp.Trim()}");

                _serverChallenge = ExtractField(resp, "challenge");
                string cliChal = GenerateRandomString(32);
                string pwHash = MD5Hash(Credentials.Password);

                // Formula from RaKS_BF2_Seeder:
                // MD5( pwHash + 48_spaces + Nickname + cliChal + srvChal + pwHash )
                string respHash = MD5Hash(pwHash + new string(' ', 48) + Credentials.Nickname + cliChal + _serverChallenge + pwHash);

                string loginPkt = $"\\login\\\\challenge\\{cliChal}\\uniquenick\\{Credentials.Nickname}\\response\\{respHash}\\port\\30000\\productid\\10493\\gamename\\battlefield2\\namespaceid\\12\\sdkrevision\\3\\id\\1\\final\\";
                byte[] loginBytes = Encoding.ASCII.GetBytes(loginPkt);

                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "TCP GPCM", $"{masterHost}:29900", loginBytes, loginBytes.Length);
                await stream.WriteAsync(loginBytes, 0, loginBytes.Length, token);

                len = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (len <= 0) return false;

                PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", "TCP GPCM", $"{masterHost}:29900", buffer, len);
                resp = Encoding.ASCII.GetString(buffer, 0, len);
                Log($"Master Server Login Response: {resp.Trim()}");

                if (resp.Contains("\\lc\\2\\"))
                {
                    SessKey = ExtractField(resp, "sesskey");
                    ProfileId = ExtractField(resp, "profileid");
                    UserId = ExtractField(resp, "userid");
                    _masterChallenge = ExtractField(resp, "proof");
                    LoginToken = ExtractField(resp, "lt");

                    uint.TryParse(ProfileId, out _profileIdNum);
                    uint.TryParse(UserId, out _userIdNum);

                    Log($"TCP Login Successful! SessKey={SessKey}, ProfileId={ProfileId}, UserId={UserId}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log($"TCP Login Exception: {ex.Message}");
                PacketLogger.LogException(Credentials.Nickname, "TCPLoginAsync", ex);
                return false;
            }
        }

        // ============================================================================
        // 2. CDKey / GPCM UDP Auth
        // ============================================================================
        private async Task<bool> DoCDKeyAuthAsync(CancellationToken token)
        {
            try
            {
                string masterHost = string.IsNullOrWhiteSpace(Server.LoginServer) ? "5.252.33.100" : Server.LoginServer;
                IPAddress masterIp = (await Dns.GetHostAddressesAsync(masterHost, token)).FirstOrDefault() ?? IPAddress.Parse("5.252.33.100");

                IPEndPoint heartbeatEP = new IPEndPoint(masterIp, 27900);
                IPEndPoint cdkeyEP = new IPEndPoint(masterIp, 29910);

                using (UdpClient authUdp = new UdpClient(0))
                {
                    authUdp.Client.ReceiveTimeout = 3000;

                    // 1. Send heartbeat to 27900
                    uint.TryParse(SessKey, out uint sessId);
                    byte[] heartbeatPkt = new byte[]
                    {
                        0x08,
                        (byte)(sessId & 0xFF),
                        (byte)((sessId >> 8) & 0xFF),
                        (byte)((sessId >> 16) & 0xFF),
                        (byte)((sessId >> 24) & 0xFF)
                    };
                    PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP HEARTBEAT", heartbeatEP.ToString(), heartbeatPkt, heartbeatPkt.Length);
                    await authUdp.SendAsync(heartbeatPkt, heartbeatPkt.Length, heartbeatEP);

                    await Task.Delay(100, token);

                    // 2. Send KeepAlive to 29910
                    byte[] cdkeyInit = GameSpyXOR("\\ka\\");
                    PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP CDKEY KA", cdkeyEP.ToString(), cdkeyInit, cdkeyInit.Length);
                    await authUdp.SendAsync(cdkeyInit, cdkeyInit.Length, cdkeyEP);

                    await Task.Delay(100, token);

                    // 3. Build CDKey Auth Request
                    _strCDKey = Credentials.CDKey.Replace("-", "").ToUpperInvariant();
                    if (string.IsNullOrEmpty(_strCDKey)) _strCDKey = GenerateRandomString(20).ToUpperInvariant();

                    _strCDKeyHash = MD5Hash(_strCDKey);
                    string challengeResp = MD5Hash(_strCDKeyHash + _serverChallenge);
                    string extra = MD5Hash(SessKey).Substring(0, 8);
                    string fullResp = _strCDKeyHash + challengeResp + extra;

                    uint localIpVal = 16777343; // 127.0.0.1
                    string authPlaintext = $"\\auth\\\\pid\\1059\\ch\\{_serverChallenge}\\resp\\{fullResp}\\ip\\{localIpVal}\\skey\\{SessKey}\\reqproof\\1\\";
                    byte[] encodedAuth = GameSpyXOR(authPlaintext);

                    PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP CDKEY AUTH", cdkeyEP.ToString(), encodedAuth, encodedAuth.Length);
                    await authUdp.SendAsync(encodedAuth, encodedAuth.Length, cdkeyEP);

                    // Receive auth response
                    UdpReceiveResult res = await authUdp.ReceiveAsync(token);
                    byte[] decodedBytes = GameSpyXOR(res.Buffer);
                    string decodedStr = Encoding.ASCII.GetString(decodedBytes);

                    PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", "UDP CDKEY AUTH RESP", res.RemoteEndPoint.ToString(), decodedBytes, decodedBytes.Length);
                    Log($"CDKey Auth Response: {decodedStr.Trim()}");

                    return decodedStr.Contains("\\uok\\");
                }
            }
            catch (Exception ex)
            {
                Log($"CDKey Auth Exception: {ex.Message}");
                PacketLogger.LogException(Credentials.Nickname, "DoCDKeyAuthAsync", ex);
                return false;
            }
        }

        // ============================================================================
        // 3. Game Server UDP Connect & Handshake
        // ============================================================================
        private async Task SendNatNegotiationAsync(CancellationToken token)
        {
            // Replicated robust NAT negotiation from the working C++ implementation.
            string masterHost = string.IsNullOrWhiteSpace(Server.LoginServer) ? "5.252.33.100" : Server.LoginServer;
            IPAddress masterIp = (await Dns.GetHostAddressesAsync(masterHost, token)).FirstOrDefault() ?? IPAddress.Parse("5.252.33.100");
            IPEndPoint natEP = new IPEndPoint(masterIp, 27900);

            Log("[NAT] Initializing NAT Negotiation / Knock with Master Server...");
            PacketLogger.LogBotEvent(Credentials.Nickname, $"NAT Knock targeting {natEP}");

            byte[] wakeupPkt = new byte[]
            {
                0x09, 0x00, 0x00, 0x00, 0x00, 0x62, 0x61, 0x74, 0x74, 0x6C, 0x65,
                0x66, 0x69, 0x65, 0x6C, 0x64, 0x32, 0x00
            };

            using (var natUdp = new UdpClient(0))
            {
                // Send initial wakeup packet
                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP NAT WAKEUP", natEP.ToString(), wakeupPkt, wakeupPkt.Length);
                await natUdp.SendAsync(wakeupPkt, wakeupPkt.Length, natEP);

                const int maxRetries = 5;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        var res = await natUdp.ReceiveAsync(token);
                        var rx = res.Buffer;
                        PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", "UDP NAT RESP", res.RemoteEndPoint.ToString(), rx, rx.Length);

                        if (rx.Length >= 3 && rx[0] == 0xFE && rx[1] == 0xFD)
                        {
                            byte respType = rx[2];
                            if (respType == 0x09)
                            {
                                Log("[NAT] SUCCESS: Master Server acknowledged presence (0x09).");
                                return;
                            }
                            else if (respType == 0x04)
                            {
                                Log("[NAT] Master Server indicated NAT detected (0x04).");
                                return;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Timeout or other error – resend wakeup and retry
                        Log($"[NAT] No response (attempt {attempt + 1}/{maxRetries}), resending wakeup packet.");
                        await natUdp.SendAsync(wakeupPkt, wakeupPkt.Length, natEP);
                    }
                }
                Log("[NAT] ERROR: Max retries reached, no response from Master Server.");
            }
        }

        // ============================================================================
        // 3. Game Server UDP Connect & Handshake
        // ============================================================================
        private async Task<bool> GameConnectAsync(CancellationToken token)
        {
            try
            {
                IPAddress gameIp = IPAddress.Parse(Server.Address);
                IPEndPoint serverEP = new IPEndPoint(gameIp, Server.Port);

                // Close old UDP and create fresh one for game server
                _udpClient?.Close();
                _udpClient = new UdpClient(0);
                _udpClient.Client.ReceiveTimeout = 5000;
                _totalBytesReceived = 0;

                Log($"[GAMECONNECT] Starting handshake with game server {serverEP}...");

                // Step 1: Send 0x11 Connect packet (78 bytes)
                byte[] connectPkt = (byte[])ConnectionPkt.Clone();
                await _udpClient.SendAsync(connectPkt, connectPkt.Length, serverEP);
                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (0x11 Connect)", serverEP.ToString(), connectPkt, connectPkt.Length);
                Log("[GAMECONNECT] Sent 0x11 Connect packet.");

                // Step 2: Wait for 0x02 Accept (raw recvfrom, only check pkt[0]==0x02, matching C++ reference)
                byte[] acceptPkt = null;
                for (int acceptAttempt = 0; acceptAttempt < 50; acceptAttempt++)
                {
                    try
                    {
                        UdpReceiveResult rawRes = await _udpClient.ReceiveAsync(token);
                        byte[] rawPkt = rawRes.Buffer;
                        if (rawPkt == null || rawPkt.Length == 0) continue;

                        _totalBytesReceived += (uint)rawPkt.Length;
                        PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", "UDP GAME (Wait 0x02 Accept)", rawRes.RemoteEndPoint.ToString(), rawPkt, rawPkt.Length);

                        if (rawPkt[0] == 0x02)
                        {
                            acceptPkt = rawPkt;
                            break;
                        }
                        else if ((rawPkt[0] & 0x0F) == 0x07 && rawPkt.Length >= 12)
                        {
                            // Handle stray TS requests while waiting for 0x02
                            byte[] tsReply = new byte[]
                            {
                                (byte)(0x08 | (rawPkt[1] & 0xF0)),
                                rawPkt[1], rawPkt[2], rawPkt[3], rawPkt[4], rawPkt[5], rawPkt[6],
                                (byte)(rawPkt[7] - 1), rawPkt[8], rawPkt[9], rawPkt[10],
                                rawPkt[11], 0x74, 0x01, 0x00, 0x00
                            };
                            await _udpClient.SendAsync(tsReply, tsReply.Length, serverEP);
                            PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (TS Reply while waiting 0x02)", serverEP.ToString(), tsReply, tsReply.Length);
                        }
                    }
                    catch (Exception) { await Task.Delay(100, token); }
                }
                if (acceptPkt == null) { Log("[GAMECONNECT] Failed to receive 0x02 Accept."); return false; }

                // Extract packetTypePrefix from buf[1] & 0xF0
                _packetTypePrefix = (byte)(acceptPkt[1] & 0xF0);
                Log($"[GAMECONNECT] Received 0x02 Accept, packetTypePrefix=0x{_packetTypePrefix:X2}");

                // Step 3: Send Session ACK (0x04 | packetTypePrefix, 0x20)
                byte[] sessionAck = new byte[] { (byte)(0x04 | _packetTypePrefix), 0x20 };
                await _udpClient.SendAsync(sessionAck, sessionAck.Length, serverEP);
                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (Session ACK)", serverEP.ToString(), sessionAck, sessionAck.Length);
                Log("[GAMECONNECT] Sent Session ACK (0x04|prefix, 0x20).");

                // Step 4: Send Timestamp Request (0x0F | prefix, 0x10, ...) — 13 bytes matching C++ reference
                byte[] tsRequest = new byte[] { (byte)(0x0F | _packetTypePrefix), 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x06, 0x00, 0x03, 0x00 };
                await _udpClient.SendAsync(tsRequest, tsRequest.Length, serverEP);
                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (TS Request)", serverEP.ToString(), tsRequest, tsRequest.Length);
                Log("[GAMECONNECT] Sent Timestamp Request (0x0F|prefix, 0x10, 13 bytes).");

                // Step 5: Loop recv — handle 0x05 disconnect, 0x07 timestamp, 0x0F/0x10 server token
                string strServerToken = string.Empty;
                bool gotTS = false, gotToken = false;
                int recvAttempts = 0;
                const int maxRecvAttempts = 50;

                while (!gotTS || !gotToken)
                {
                    if (recvAttempts >= maxRecvAttempts) { Log("[GAMECONNECT] Timeout waiting for TS/Token."); return false; }
                    recvAttempts++;

                    byte[] pkt;
                    try
                    {
                        UdpReceiveResult res = await _udpClient.ReceiveAsync(token);
                        pkt = res.Buffer;
                    }
                    catch (Exception) { await Task.Delay(100, token); continue; }

                    _totalBytesReceived += (uint)pkt.Length;
                    PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", "UDP GAME (Handshake)", serverEP.ToString(), pkt, pkt.Length);

                    byte pktType = (byte)(pkt[0] & 0x0F);

                    if (pktType == 0x05)
                    {
                        Log("[GAMECONNECT] Server sent kick/disconnect (0x05) during handshake!");
                        return false;
                    }
                    else if (pktType == 0x07 && pkt.Length >= 12)
                    {
                        gotTS = true;
                        // Match C++ GameConnect TS ACK exactly: hardcoded bytes 1-6, 15 bytes total
                        byte[] tsReply = new byte[]
                        {
                            (byte)(0x08 | _packetTypePrefix),
                            0xF0, 0x03, 0x00, 0x00, 0x00, 0x00,
                            pkt[7],
                            pkt[8], pkt[9], pkt[10],
                            0x74, 0x01, 0x00, 0x00, 0x00
                        };
                        await _udpClient.SendAsync(tsReply, tsReply.Length, serverEP);
                        PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (TS ACK)", serverEP.ToString(), tsReply, tsReply.Length);
                        Log("[GAMECONNECT] Sent Timestamp ACK (15 bytes, hardcoded seq).");
                    }
                    else if (pktType == 0x0F && pkt.Length > 1 && pkt[1] == 0x10)
                    {
                        char[] tokenChars = new char[13];
                        for (int j = 0; j < 13 && (12 + j) < pkt.Length; j++)
                        {
                            byte stripped = (byte)(pkt[12 + j] & 0x7F);
                            if (stripped == 0) break;
                            tokenChars[j] = (char)stripped;
                        }
                        strServerToken = new string(tokenChars).TrimEnd('\0');
                        Log($"[GAMECONNECT] Received Server Token: {strServerToken}");
                        gotToken = true;
                    }
                }

                // Step 6: Build and send P7 (nibble-encoded 72-char auth)
                byte[] p7 = BuildP7(_packetTypePrefix, _strCDKeyHash, strServerToken);
                await _udpClient.SendAsync(p7, p7.Length, serverEP);
                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (P7 Auth)", serverEP.ToString(), p7, p7.Length);
                Log("[GAMECONNECT] Sent P7 Auth packet.");

                // Step 7: Wait for 0x0F/0x20 response
                UdpReceiveResult p7Res = await WaitForPacketAsync(0x0F, 0x20, 50, token);
                if (p7Res.Buffer == null) { Log("[GAMECONNECT] Failed to receive P7 response (0x0F/0x20)."); return false; }
                byte[] p7Resp = p7Res.Buffer;
                Log("[GAMECONNECT] Received P7 Response (0x0F/0x20).");

                // Step 8: Send Auth ACK (0x07 | prefix, seq bytes, totalBytesReceived, trailing)
                byte[] authAck = new byte[12];
                authAck[0] = (byte)(0x07 | _packetTypePrefix);
                Array.Copy(p7Resp, 1, authAck, 1, 6);
                authAck[7] = (byte)((_totalBytesReceived >> 0) & 0xFF);
                authAck[8] = (byte)((_totalBytesReceived >> 8) & 0xFF);
                authAck[9] = (byte)((_totalBytesReceived >> 16) & 0xFF);
                authAck[10] = (byte)((_totalBytesReceived >> 24) & 0xFF);
                authAck[11] = (byte)((p7Resp[0] & 0xF0) | 0x02);
                await _udpClient.SendAsync(authAck, authAck.Length, serverEP);
                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (Auth ACK)", serverEP.ToString(), authAck, authAck.Length);
                Log("[GAMECONNECT] Sent Auth ACK.");

                // Step 9: Wait for 0x07/0x60
                UdpReceiveResult authConfirmRes = await WaitForPacketAsync(0x07, 0x60, 50, token);
                if (authConfirmRes.Buffer == null) { Log("[GAMECONNECT] Failed to receive Auth Confirm (0x07/0x60)."); return false; }
                Log("[GAMECONNECT] Received Auth Confirm (0x07/0x60).");

                // Step 10: Calculate legit token and build P10
                byte[] legitToken = CalculateLegitToken(Credentials.Nickname, _profileIdNum);
                byte[] p10 = BuildP10(_packetTypePrefix, Credentials.Nickname, legitToken, LoginToken);
                await _udpClient.SendAsync(p10, p10.Length, serverEP);
                PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (P10 Confirm)", serverEP.ToString(), p10, p10.Length);
                Log("[GAMECONNECT] Sent P10 Player Confirmation packet.");

                // Step 11: Wait for 0x0F/0x90
                UdpReceiveResult p10Res = await WaitForPacketAsync(0x0F, 0x90, 50, token);
                if (p10Res.Buffer == null) { Log("[GAMECONNECT] Failed to receive P10 Response (0x0F/0x90)."); return false; }
                Log("[GAMECONNECT] Received P10 Response (0x0F/0x90) — CONNECTED!");

                SetState(ConnectionState.Connected);
                Log($"*** {Credentials.Nickname} CONNECTED SUCCESSFULLY TO GAME SERVER ***");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[GAMECONNECT] Exception: {ex.Message}");
                PacketLogger.LogException(Credentials.Nickname, "GameConnectAsync", ex);
                return false;
            }
        }

        // ============================================================================
        // 4. Active Game Loop (KeepAlive & ACK Engine)
        // ============================================================================
        private async Task GameLoopAsync(CancellationToken token)
        {
            Log("Beginning KeepAlive Game Loop...");
            IPAddress gameIp = IPAddress.Parse(Server.Address);
            IPEndPoint serverEP = new IPEndPoint(gameIp, Server.Port);

            while (!token.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    UdpReceiveResult rx = await _udpClient.ReceiveAsync(token);
                    byte[] pkt = rx.Buffer;
                    _totalBytesReceived += (uint)pkt.Length;
                    PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", "UDP GAME LOOP", rx.RemoteEndPoint.ToString(), pkt, pkt.Length);

                    byte pktType = (byte)(pkt[0] & 0x0F);

                    if (pktType == 0x07 && pkt.Length >= 11)
                    {
                        uint serverTS = BitConverter.ToUInt32(pkt, 7);
                        uint replyTS = serverTS - 1;

                        List<byte> tsReply = new List<byte> { (byte)(0x08 | _packetTypePrefix) };
                        for (int i = 1; i <= 6; i++) tsReply.Add(pkt[i]);
                        tsReply.AddRange(BitConverter.GetBytes(replyTS));
                        tsReply.AddRange(new byte[] { 0x74, 0x01, 0x00, 0x00, 0x00 });

                        await _udpClient.SendAsync(tsReply.ToArray(), tsReply.Count, serverEP);
                        PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (Loop TS Reply)", serverEP.ToString(), tsReply.ToArray(), tsReply.Count);

                        if (State != ConnectionState.Connected)
                        {
                            SetState(ConnectionState.Connected);
                            Log($"*** {Credentials.Nickname} CONNECTED SUCCESSFULLY TO GAME SERVER ***");
                        }
                    }
                    else if (pktType == 0x0F && pkt.Length >= 7)
                    {
                        List<byte> dataAck = new List<byte>
                        {
                            (byte)(0x07 | _packetTypePrefix),
                            pkt[1], pkt[2], pkt[3], pkt[4], pkt[5], pkt[6]
                        };
                        dataAck.Add((byte)((_totalBytesReceived >> 0) & 0xFF));
                        dataAck.Add((byte)((_totalBytesReceived >> 8) & 0xFF));
                        dataAck.Add((byte)((_totalBytesReceived >> 16) & 0xFF));
                        dataAck.Add((byte)((_totalBytesReceived >> 24) & 0xFF));
                        dataAck.Add((byte)((pkt[0] & 0xF0) | 0x02));

                        await _udpClient.SendAsync(dataAck.ToArray(), dataAck.Count, serverEP);
                        PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (Data ACK)", serverEP.ToString(), dataAck.ToArray(), dataAck.Count);
                    }
                    else if (pktType == 0x05)
                    {
                        Log("Server sent Disconnect/Kick (0x05)!");
                        SetState(ConnectionState.Disconnected);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        PacketLogger.LogException(Credentials.Nickname, "GameLoopAsync", ex);
                    }
                }
            }

            // If we exit the loop and weren't cancelled, the connection was lost
            if (!token.IsCancellationRequested)
            {
                Log("Game loop exited unexpectedly. Connection lost.");
                SetState(ConnectionState.Disconnected);
            }
        }

        // ============================================================================
        // Helper Methods
        // ============================================================================
        private async Task<UdpReceiveResult> WaitForPacketAsync(byte expectedType, byte expectedSubtype, int maxRetries, CancellationToken token)
        {
            if (_udpClient == null) return default;
            IPAddress gameIp = IPAddress.Parse(Server.Address);
            IPEndPoint serverEP = new IPEndPoint(gameIp, Server.Port);

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(token);
                    byte[] pkt = result.Buffer;
                    if (pkt == null || pkt.Length == 0) continue;

                    _totalBytesReceived += (uint)pkt.Length;
                    PacketLogger.LogPacket(Credentials.Nickname, "[IN <- RECV]", $"WAIT (Exp {expectedType:X2}/{expectedSubtype:X2})", result.RemoteEndPoint.ToString(), pkt, pkt.Length);

                    byte pktType = (byte)(pkt[0] & 0x0F);
                    byte pktSubtype = pkt.Length > 1 ? pkt[1] : (byte)0;

                    if (pktType == expectedType && pktSubtype == expectedSubtype)
                    {
                        if (pktType == 0x07 && pktSubtype != 0x60 && pkt.Length >= 12)
                        {
                            byte[] tsReply = new byte[]
                            {
                                (byte)(0x08 | _packetTypePrefix),
                                pkt[1], pkt[2], pkt[3], pkt[4], pkt[5], pkt[6],
                                (byte)(pkt[7] - 1), pkt[8], pkt[9], pkt[10],
                                pkt[11], 0x74, 0x01, 0x00, 0x00
                            };
                            await _udpClient.SendAsync(tsReply, tsReply.Length, serverEP);
                        }
                        // NOTE: C++ WaitForPacket has dead code for 0x0F ACK handling
                        // (pktType == (0x0F | packetTypePrefix) is always false).
                        // We intentionally do NOT send ACKs for 0x0F packets here.
                        return result;
                    }
                    else if (pktType == 0x07 && pkt.Length >= 12)
                    {
                        byte[] tsReply = new byte[]
                        {
                            (byte)(0x08 | _packetTypePrefix),
                            pkt[1], pkt[2], pkt[3], pkt[4], pkt[5], pkt[6],
                            (byte)(pkt[7] - 1), pkt[8], pkt[9], pkt[10],
                            pkt[11], 0x74, 0x01, 0x00, 0x00
                        };
                        await _udpClient.SendAsync(tsReply, tsReply.Length, serverEP);
                        PacketLogger.LogPacket(Credentials.Nickname, "[OUT -> SEND]", "UDP GAME (Wait TS Reply)", serverEP.ToString(), tsReply, tsReply.Length);
                    }
                    else if (pkt[0] == 0x05)
                    {
                        Log("Server sent Disconnect/Kick (0x05) while waiting!");
                        return default;
                    }
                }
                catch (Exception)
                {
                    await Task.Delay(10, token);
                }
            }
            return default;
        }

        private byte[] BuildP7(byte sessNibble, string cdKeyHash, string serverToken)
        {
            string md5CdKey = MD5Hash(cdKeyHash);
            uint clientToken = (uint)Random.Shared.Next();
            string clientTokenHex = clientToken.ToString("x8");

            string combined = md5CdKey + "14673" + serverToken;
            string md5Combined = MD5Hash(combined);

            string auth72 = md5CdKey + clientTokenHex + md5Combined;

            byte[] payload = new byte[72];
            for (int i = 0; i < 72; i++)
            {
                char c = auth72[i];
                int val = (c >= 'a' && c <= 'f') ? (c - 'a' + 10) : (c - '0');
                payload[i] = (byte)(0x10 | (val & 0x0F));
            }

            List<byte> pkt = new List<byte>();
            pkt.Add((byte)(sessNibble | 0x0F));
            pkt.Add(0x20);
            byte[] hdr = new byte[] { 0x04, 0x01, 0x00, 0x00, 0x00, 0x58, 0x00, 0x06, 0x04, 0x02 };
            pkt.AddRange(hdr);
            pkt.AddRange(payload);
            byte[] tail = new byte[] { 0x00, 0xB3, 0x36, 0xBB, 0x38, 0x80, 0x28, 0x86, 0x0A, 0x23, 0x04, 0x00, 0x00 };
            pkt.AddRange(tail);
            return pkt.ToArray();
        }

        private byte[] BuildP10(byte sessNibble, string playerName, byte[] authToken8, string proofHex)
        {
            byte nameLen = (byte)Encoding.ASCII.GetByteCount(playerName);
            List<byte> pkt = new List<byte>();
            pkt.Add((byte)(sessNibble | 0x0F));
            pkt.Add(0x30);
            pkt.AddRange(new byte[] { 0x08, 0x03, 0x00, 0x00, 0x00 });
            pkt.Add((byte)(nameLen + 53));
            pkt.AddRange(new byte[] { 0x00, 0x0A, 0x08, 0x84, 0x01, 0x00, 0x00, 0x00 });
            pkt.Add((byte)(nameLen + 39));
            pkt.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x04 });
            pkt.Add((byte)(nameLen + 39));
            pkt.Add(nameLen);
            pkt.Add(0x00);
            pkt.AddRange(Encoding.ASCII.GetBytes(playerName));
            pkt.AddRange(authToken8);
            pkt.AddRange(new byte[] { 0x00, 0x00, 0x18, 0x00 });
            pkt.AddRange(Encoding.ASCII.GetBytes(proofHex));
            pkt.Add(0x01);
            pkt.Add(0x02);
            return pkt.ToArray();
        }

        private static byte[] CalculateLegitToken(string playerName, uint profileId)
        {
            uint f1 = 0x1505;
            foreach (char c in playerName)
            {
                uint v = (byte)c;
                if (v >= 0x41 && v <= 0x5A) v += 0x20;
                f1 = (f1 * 0x21) ^ v;
            }
            uint f2 = profileId * 2;
            return new byte[]
            {
                (byte)(f1 & 0xFF), (byte)((f1 >> 8) & 0xFF), (byte)((f1 >> 16) & 0xFF), (byte)((f1 >> 24) & 0xFF),
                (byte)(f2 & 0xFF), (byte)((f2 >> 8) & 0xFF), (byte)((f2 >> 16) & 0xFF), (byte)((f2 >> 24) & 0xFF)
            };
        }

        private static byte[] GameSpyXOR(byte[] input)
        {
            byte[] cipher = new byte[] { 103, 97, 109, 101, 115, 112, 121 }; // "gamespy"
            byte[] result = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                result[i] = (byte)(input[i] ^ cipher[i % 7]);
            }
            return result;
        }

        private static byte[] GameSpyXOR(string data)
        {
            return GameSpyXOR(Encoding.ASCII.GetBytes(data));
        }

        private static string MD5Hash(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.ASCII.GetBytes(input));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in bytes)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ExtractField(string packet, string fieldName)
        {
            string key = $"\\{fieldName}\\";
            int start = packet.IndexOf(key, StringComparison.Ordinal);
            if (start == -1) return string.Empty;
            start += key.Length;
            int end = packet.IndexOf('\\', start);
            return end == -1 ? packet.Substring(start) : packet.Substring(start, end - start);
        }

        private static string GenerateRandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            Random rng = new Random();
            char[] result = new char[length];
            for (int i = 0; i < length; i++)
                result[i] = chars[rng.Next(chars.Length)];
            return new string(result);
        }

        private void SetState(ConnectionState newState)
        {
            State = newState;
            OnStateChanged?.Invoke(Credentials.Nickname, newState);

            if (newState == ConnectionState.Connected)
            {
                _connectTcs?.TrySetResult(true);
            }
            else if (newState == ConnectionState.Error || newState == ConnectionState.Disconnected)
            {
                _connectTcs?.TrySetResult(false);
            }
        }

        private void Log(string message)
        {
            OnLogMessage?.Invoke(Credentials.Nickname, message);
        }

        private void CleanupSockets()
        {
            _tcpClient?.Close();
            _udpClient?.Close();
        }
    }
}