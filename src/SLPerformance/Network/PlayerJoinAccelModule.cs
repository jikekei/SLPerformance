using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CentralAuth;
using Cryptography;
using GameCore;
using HarmonyLib;
using LabApi.Events;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LiteNetLib;
using Mirror;
using Mirror.LiteNetLib4Mirror;
using NetworkManagerUtils.Dummies;
using NorthwoodLib;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Security;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;
using Utf8Json;

namespace SLPerformance.Network;

/// <summary>
/// OPT-22: Player Join &amp; Authentication Spike Accelerator + Deep Join Profiler (`slperf joinstats`).
///
/// Eliminates the two real-player join TPS drop spikes on SCP:SL Dedicated Servers:
/// 1. Background Pre-Generated ECDH P-384 Keypair Pool (`EncryptedChannelManager.PrepareExchange` + `ECDSA.KeyToString`):
///    Pre-generates fresh CSPRNG NIST P-384 keypairs, ECDHBasicAgreement instances, and PEM public strings on a
///    background thread so `RequestAuthentication` takes &lt;0.01ms instead of ~15-20ms on the Unity main thread.
/// 2. Async Background Crypto Offload for `PlayerAuthenticationManager.ProcessAuthenticationResponse`:
///    Offloads the 2-3 pure-CPU BouncyCastle NIST P-384 ECDSA signature verifications (`SignedAuthToken`,
///    `EcdhPublicKeySignature`, `SignedBadgeToken`) and P-384 `ECDH.DeriveKey` scalar multiplication (~50-80ms on Mono)
///    to bounded background workers while keeping 100% bit-identical BouncyCastle verification, fail-closed disconnect
///    checks, and exact main-thread execution of `CheckBans`, `FinalizeAuthentication`, badge assignment, and `OnJoined`.
/// 3. Eliminates the redundant 4th P-384 ECDSA verification in `ServerRoles.Update()` when applying a player's
///    already-verified global badge token on the server.
/// 4. Lock-free In-Memory Mtime-Cached `BanHandler.QueryBan` (`UserIdBans.txt` / `IpBans.txt`):
///    Eliminates synchronous disk reads and O(N) string splitting on every preauth &amp; auth, while automatically
///    invalidating on `IssueBan` / `RemoveBan` / `ValidateBans` / `Init` and external file modification (&lt;=1s).
/// 5. Static Compiled Regex Cache in `NicknameSync.Start()`:
///    Prevents per-player `RegexOptions.Compiled` dynamic IL assembly JIT when `nickname_filter` is configured.
/// 6. Single-Writer Zero-Copy `Mirror.NetworkServer.SendSpawnMessage`:
///    Eliminates redundant `ownerWriter` + per-`NetworkBehaviour` temporary writer rentals and byte copies when
///    spawning 600-1200+ map objects during `SpawnObserversForConnection`.
/// 7. Built-in Zero-Overhead Real-Player Join Profiler (`slperf joinstats`):
///    Times every phase of real-player join (`AddPlayer`, `SpawnObs`, `ReqAuth`, `AuthEnqueue`, `AuthWorkerCrypto`,
///    `CheckBans`, `FinalizeAuth`, `BadgeApply`, `OnJoined`).
/// </summary>
public sealed class PlayerJoinAccelModule : OptimizationModuleBase
{
    public override string Id => "auth.join_spike_accel";
    public override string Name => "Player Join & Async Auth Spike Accelerator";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Background ECDH P-384 keypair pool, async BouncyCastle auth/badge crypto offload, redundant ServerRoles badge re-verify skip, in-memory BanHandler cache, NicknameSync compiled Regex cache, single-writer SendSpawnMessage, and `slperf joinstats`.";

    private delegate void SetAuthTokenDelegate(ref AuthenticationResponse resp, AuthenticationToken token);
    private delegate void SetBadgeTokenDelegate(ref AuthenticationResponse resp, BadgeToken token);
    private delegate void RejectAuthDelegate(PlayerAuthenticationManager instance, string reason, string userId, bool removeSalt);
    private delegate bool CheckBansDelegate(PlayerAuthenticationManager instance, AuthenticationToken token, string unsalted);

    private sealed class PreGeneratedEcdh
    {
        public readonly AsymmetricCipherKeyPair Keys;
        public readonly ECDHBasicAgreement Agreement;
        public readonly string PublicPem;

        public PreGeneratedEcdh(AsymmetricCipherKeyPair keys, ECDHBasicAgreement agreement, string publicPem)
        {
            Keys = keys;
            Agreement = agreement;
            PublicPem = publicPem;
        }
    }

    private sealed class AuthCryptoJob
    {
        // Immutable main-thread inputs
        public PlayerAuthenticationManager AuthManager;
        public ReferenceHub Hub;
        public NetworkConnectionToClient Connection;
        public AuthenticationResponse Msg;
        public string Challenge;
        public int PlayerId;
        public bool IsLocalPlayer;
        public IPEndPoint RemoteEndPoint;
        public AsymmetricKeyParameter ServerPublicKey;
        public bool IsTestServer;
        public bool IsPrivateBeta;
        public System.Version ServerVersion;
        public ECDHBasicAgreement EcdhExchange;
        public bool NeedsEcdhDerive;
        public JoinTrace ActiveTrace;
        public long EnqueueTimestamp;

        // Pure-CPU background worker outputs
        public bool AuthOk;
        public AuthenticationToken AuthToken;
        public string AuthError;
        public string AuthUserId;
        public Exception AuthCryptoException;

        public bool EarlyRejectedBeforeEcdh;
        public bool EcdhSigOk;
        public Exception EcdhSigException;
        public byte[] DerivedEncryptionKey;

        public bool BadgeChecked;
        public bool BadgeOk;
        public BadgeToken BadgeToken;
        public string BadgeError;
        public Exception BadgeCryptoException;

        public Exception WorkerException;
        public long WorkerCryptoTicks;
    }

    internal sealed class BanTableSnapshot
    {
        public readonly string Path;
        public readonly DateTime LastWriteUtc;
        public readonly long FileLength;
        public readonly Dictionary<string, BanDetails> ById;

        public BanTableSnapshot(string path, DateTime lastWriteUtc, long fileLength, Dictionary<string, BanDetails> byId)
        {
            Path = path;
            LastWriteUtc = lastWriteUtc;
            FileLength = fileLength;
            ById = byId;
        }
    }

    internal sealed class BanCacheBucket
    {
        public readonly BanHandler.BanType Type;
        public readonly object ReloadLock = new object();
        public BanTableSnapshot Snapshot;
        public long LastStatTicks;
        public int ForceReload = 1;

        public BanCacheBucket(BanHandler.BanType type)
        {
            Type = type;
        }
    }

    private sealed class JoinTrace
    {
        public DateTime TimestampUtc;
        public int ConnectionId;
        public int PlayerId;
        public string UserId;
        public long AddPlayerTicks;
        public long SpawnObsTicks;
        public int SpawnObsCount;
        public long ReqAuthTicks;
        public bool EcdhPoolHit;
        public long AuthEnqueueTicks;
        public long AuthWorkerCryptoTicks;
        public long AuthTotalWaitTicks;
        public long CheckBansTicks;
        public long FinalizeAuthTicks;
        public long FinalizeUserIdTicks;
        public long FinalizeModeTicks;
        public long FinalizeNickTicks;
        public long FinalizeWelcomeTicks;
        public long FinalizePermsTicks;
        public long BadgeApplyTicks;
        public long OnJoinedTicks;
        public string SlowFinalizeBreakdown;
        public string SlowOnJoinedBreakdown;
        public bool Offloaded;
        public bool Completed;
    }

    private const int JoinHistoryCapacity = 10;
    private const int MaxDrainPerFrame = 4;
    private const double MaxDrainMsPerFrame = 4.0;
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
    private static readonly long OneSecondTicks = Stopwatch.Frequency;

    // Compiled accessors — EncryptedChannelManager
    private static Func<EncryptedChannelManager, ECDHBasicAgreement> _getExchange;
    private static Action<EncryptedChannelManager, ECDHBasicAgreement> _setExchange;
    private static Action<EncryptedChannelManager, AsymmetricCipherKeyPair> _setEcdhKeys;
    private static Func<EncryptedChannelManager, byte[]> _getEncryptionKey;
    private static Action<EncryptedChannelManager, byte[]> _setEncryptionKey;
    private static Func<bool> _getCryptoDebug;
    private static Action<EncryptedChannelManager, string> _callServerProcessExchange;

    // Compiled accessors — PlayerAuthenticationManager
    private static Func<PlayerAuthenticationManager, bool> _getAuthRequested;
    private static Func<PlayerAuthenticationManager, string> _getChallenge;
    private static Action<PlayerAuthenticationManager, string> _setChallenge;
    private static Func<PlayerAuthenticationManager, float> _getTimeoutTimer;
    private static Func<PlayerAuthenticationManager, ReferenceHub> _getAuthHub;
    private static Action<PlayerAuthenticationManager, AuthenticationResponse> _setAuthResponse;
    private static SetAuthTokenDelegate _setMsgAuthToken;
    private static SetBadgeTokenDelegate _setMsgBadgeToken;
    private static RejectAuthDelegate _rejectAuth;
    private static Action<PlayerAuthenticationManager, string> _rejectBadgeToken;
    private static Action<PlayerAuthenticationManager> _finalizeAuth;
    private static CheckBansDelegate _checkBans;
    private static Action<PlayerAuthenticationManager, string> _setPrivUserId;
    private static Func<PlayerAuthenticationManager, ClientInstanceMode> _getTargetInstanceMode;
    private static Action<PlayerAuthenticationManager, ClientInstanceMode> _setTargetInstanceMode;
    private static Action<PlayerAuthenticationManager, bool> _setDoNotTrack;
    private static Action<PlayerAuthenticationManager> _callRefreshSyncedId;
    private static Func<bool> _getAllowSameAccountJoining;
    private static Func<Action<ReferenceHub>> _getOnSyncedUserIdAssigned;
    private static Func<Action<ReferenceHub, ClientInstanceMode>> _getOnInstanceModeChanged;
    private static Func<LabEventHandler<PlayerJoinedEventArgs>> _getPlayerJoinedEvent;
    private static MethodInfo _finalizeAuthMethod;
    private static MethodInfo _onJoinedMethod;

    // Compiled accessors — ServerRoles
    private static Func<ServerRoles, ReferenceHub> _getServerRolesHub;
    private static Func<ServerRoles, string> _getServerRolesPrevBadge;
    private static Action<ServerRoles, string> _setServerRolesPrevBadge;
    private static Action<ServerRoles, string> _setServerRolesBgc;
    private static Action<ServerRoles, string> _setServerRolesBgt;
    private static Action<ServerRoles, bool> _setServerRolesAuthorizeBadge;
    private static Action<ServerRoles, string> _setServerRolesMyText;
    private static Func<ServerRoles, bool> _getServerRolesBadgeCover;
    private static Func<ServerRoles, ulong> _getServerRolesGlobalPerms;
    private static Action<ServerRoles, ulong> _setServerRolesGlobalPerms;

    // Compiled accessors — NicknameSync
    private static Action<NicknameSync, ReferenceHub> _setNickHub;
    private static Action<NicknameSync, Regex> _setNickFilter;
    private static Action<NicknameSync, string> _setNickReplacement;
    private static Action<NicknameSync, string> _callSetNick;

    // Compiled accessors — Mirror SendSpawnMessage
    private static Action<NetworkIdentity> _validateComponents;
    private static Action<NetworkBehaviour, NetworkWriter, bool> _serializeBehaviour;

    // Module state
    private static volatile bool _active;
    private static bool _asyncAuthEnabled;
    private static int _ecdhTargetPoolSize = 8;
    private static int _maxAuthWorkers = 2;
    private static System.Version _cachedServerVersion;
    private static bool _subEcdhPoolActive;
    private static bool _subAsyncAuthActive;
    private static bool _subBadgeSkipActive;
    private static bool _subBanCacheActive;
    private static bool _subNickRegexActive;
    private static bool _subSendSpawnActive;

    // Component 1: ECDH Pre-Gen Pool
    private static readonly ConcurrentQueue<PreGeneratedEcdh> EcdhPool = new ConcurrentQueue<PreGeneratedEcdh>();
    private static readonly ConcurrentDictionary<AsymmetricKeyParameter, string> PreformattedPem = new ConcurrentDictionary<AsymmetricKeyParameter, string>();
    private static int _ecdhRefillRunning;
    private static long _ecdhPoolHits;
    private static long _ecdhPoolMisses;
    private static long _ecdhPemHits;

    // Component 2: Async Auth Worker Queue
    private static readonly HashSet<PlayerAuthenticationManager> InFlightAuth = new HashSet<PlayerAuthenticationManager>();
    private static readonly ConcurrentQueue<AuthCryptoJob> PendingAuthJobs = new ConcurrentQueue<AuthCryptoJob>();
    private static readonly ConcurrentQueue<AuthCryptoJob> CompletedAuthJobs = new ConcurrentQueue<AuthCryptoJob>();
    private static int _activeAuthWorkers;
    private static long _asyncAuthOffloaded;
    private static long _asyncAuthCompleted;
    private static long _asyncAuthDiscarded;
    private static long _asyncAuthFallbackSync;
    private static double _totalSavedWorkerCryptoMs;

    // Component 3: GlobalBadge re-verify skip counter
    private static long _globalBadgeReverifySkips;

    // Component 4: BanHandler in-memory cache
    private static readonly BanCacheBucket UserIdBanBucket = new BanCacheBucket(BanHandler.BanType.UserId);
    private static readonly BanCacheBucket IpBanBucket = new BanCacheBucket(BanHandler.BanType.IP);
    private static long _banCacheHits;
    private static long _banCacheReloads;

    // Component 5: NicknameSync static Regex cache
    private static string _cachedNickFilterPattern;
    private static Regex _cachedNickFilterRegex;
    private static long _nickRegexCacheHits;
    private static long _nickRegexCompiledCount;

    // Component 6: SendSpawnMessage fast counter
    private static long _sendSpawnFastCalls;

    // Component 7: Join Profiler state (main thread only)
    private static readonly Dictionary<int, JoinTrace> ActiveTracesByConnId = new Dictionary<int, JoinTrace>(32);
    private static readonly JoinTrace[] JoinHistory = new JoinTrace[JoinHistoryCapacity];
    private static int _joinHistoryHead;
    private static int _joinHistoryCount;
    private static int _spawnObsActiveConnId = -1;
    private static int _spawnObsCounter;
    private static bool _lastPrepareExchangePoolHit;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.AuthJoinSpikeAccel;

    internal static bool EnsureAccessorsCompiled()
    {
        if (_validateComponents != null && _serializeBehaviour != null && _getExchange != null && _setMsgAuthToken != null)
        {
            return true;
        }

        Type ecmType = typeof(EncryptedChannelManager);
        _getExchange = ReflectionHelpers.CreateInstanceFieldGetter<EncryptedChannelManager, ECDHBasicAgreement>(ecmType, "_exchange");
        _setExchange = ReflectionHelpers.CreateInstanceFieldSetter<EncryptedChannelManager, ECDHBasicAgreement>(ecmType, "_exchange");
        _setEcdhKeys = ReflectionHelpers.CreateInstanceFieldSetter<EncryptedChannelManager, AsymmetricCipherKeyPair>(ecmType, "<EcdhKeys>k__BackingField");
        _getEncryptionKey = ReflectionHelpers.CreateInstanceFieldGetter<EncryptedChannelManager, byte[]>(ecmType, "EncryptionKey");
        _setEncryptionKey = ReflectionHelpers.CreateInstanceFieldSetter<EncryptedChannelManager, byte[]>(ecmType, "EncryptionKey");
        _getCryptoDebug = ReflectionHelpers.CreateStaticFieldGetter<bool>(ecmType, "CryptographyDebug");
        _callServerProcessExchange = ReflectionHelpers.CreateOpenDelegate<Action<EncryptedChannelManager, string>>(
            AccessTools.Method(ecmType, "ServerProcessExchange", new[] { typeof(string) }));

        Type pamType = typeof(PlayerAuthenticationManager);
        _getAuthRequested = ReflectionHelpers.CreateInstanceFieldGetter<PlayerAuthenticationManager, bool>(pamType, "_authenticationRequested");
        _getChallenge = ReflectionHelpers.CreateInstanceFieldGetter<PlayerAuthenticationManager, string>(pamType, "_challenge");
        _setChallenge = ReflectionHelpers.CreateInstanceFieldSetter<PlayerAuthenticationManager, string>(pamType, "_challenge");
        _getTimeoutTimer = ReflectionHelpers.CreateInstanceFieldGetter<PlayerAuthenticationManager, float>(pamType, "_timeoutTimer");
        _getAuthHub = ReflectionHelpers.CreateInstanceFieldGetter<PlayerAuthenticationManager, ReferenceHub>(pamType, "_hub");
        _setAuthResponse = ReflectionHelpers.CreateInstanceFieldSetter<PlayerAuthenticationManager, AuthenticationResponse>(pamType, "<AuthenticationResponse>k__BackingField");
        _setMsgAuthToken = CreateStructPropertySetter<SetAuthTokenDelegate>(typeof(AuthenticationResponse), nameof(AuthenticationResponse.AuthToken), typeof(AuthenticationToken));
        _setMsgBadgeToken = CreateStructPropertySetter<SetBadgeTokenDelegate>(typeof(AuthenticationResponse), nameof(AuthenticationResponse.BadgeToken), typeof(BadgeToken));
        _rejectAuth = ReflectionHelpers.CreateOpenDelegate<RejectAuthDelegate>(
            AccessTools.Method(pamType, "RejectAuthentication", new[] { typeof(string), typeof(string), typeof(bool) }));
        _rejectBadgeToken = ReflectionHelpers.CreateOpenDelegate<Action<PlayerAuthenticationManager, string>>(
            AccessTools.Method(pamType, "RejectBadgeToken", new[] { typeof(string) }));
        _finalizeAuthMethod = AccessTools.Method(pamType, "FinalizeAuthentication", Type.EmptyTypes);
        _finalizeAuth = ReflectionHelpers.CreateOpenDelegate<Action<PlayerAuthenticationManager>>(_finalizeAuthMethod);
        _checkBans = ReflectionHelpers.CreateOpenDelegate<CheckBansDelegate>(
            AccessTools.Method(pamType, "CheckBans", new[] { typeof(AuthenticationToken), typeof(string) }));
        _setPrivUserId = ReflectionHelpers.CreateInstanceFieldSetter<PlayerAuthenticationManager, string>(pamType, "_privUserId");
        _getTargetInstanceMode = ReflectionHelpers.CreateInstanceFieldGetter<PlayerAuthenticationManager, ClientInstanceMode>(pamType, "_targetInstanceMode");
        _setTargetInstanceMode = ReflectionHelpers.CreateInstanceFieldSetter<PlayerAuthenticationManager, ClientInstanceMode>(pamType, "_targetInstanceMode");
        _setDoNotTrack = ReflectionHelpers.CreateInstanceFieldSetter<PlayerAuthenticationManager, bool>(pamType, "<DoNotTrack>k__BackingField");
        _callRefreshSyncedId = ReflectionHelpers.CreateOpenDelegate<Action<PlayerAuthenticationManager>>(
            AccessTools.Method(pamType, "RefreshSyncedId", Type.EmptyTypes));
        _getAllowSameAccountJoining = ReflectionHelpers.CreateStaticFieldGetter<bool>(pamType, "AllowSameAccountJoining");
        _getOnSyncedUserIdAssigned = ReflectionHelpers.CreateStaticFieldGetter<Action<ReferenceHub>>(pamType, nameof(PlayerAuthenticationManager.OnSyncedUserIdAssigned));
        _getOnInstanceModeChanged = ReflectionHelpers.CreateStaticFieldGetter<Action<ReferenceHub, ClientInstanceMode>>(pamType, nameof(PlayerAuthenticationManager.OnInstanceModeChanged));
        _getPlayerJoinedEvent = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerJoinedEventArgs>>(typeof(PlayerEvents), nameof(PlayerEvents.Joined));
        _onJoinedMethod = AccessTools.Method(typeof(PlayerEvents), nameof(PlayerEvents.OnJoined), new[] { typeof(PlayerJoinedEventArgs) });

        Type srType = typeof(ServerRoles);
        _getServerRolesHub = ReflectionHelpers.CreateInstanceFieldGetter<ServerRoles, ReferenceHub>(srType, "_hub");
        _getServerRolesPrevBadge = ReflectionHelpers.CreateInstanceFieldGetter<ServerRoles, string>(srType, "_prevBadge");
        _setServerRolesPrevBadge = ReflectionHelpers.CreateInstanceFieldSetter<ServerRoles, string>(srType, "_prevBadge");
        _setServerRolesBgc = ReflectionHelpers.CreateInstanceFieldSetter<ServerRoles, string>(srType, "_bgc");
        _setServerRolesBgt = ReflectionHelpers.CreateInstanceFieldSetter<ServerRoles, string>(srType, "_bgt");
        _setServerRolesAuthorizeBadge = ReflectionHelpers.CreateInstanceFieldSetter<ServerRoles, bool>(srType, "_authorizeBadge");
        _setServerRolesMyText = ReflectionHelpers.CreateInstanceFieldSetter<ServerRoles, string>(srType, "<MyText>k__BackingField");
        _getServerRolesBadgeCover = ReflectionHelpers.CreateInstanceFieldGetter<ServerRoles, bool>(srType, "BadgeCover");
        _getServerRolesGlobalPerms = ReflectionHelpers.CreateInstanceFieldGetter<ServerRoles, ulong>(srType, "GlobalPerms");
        _setServerRolesGlobalPerms = ReflectionHelpers.CreateInstanceFieldSetter<ServerRoles, ulong>(srType, "GlobalPerms");

        Type nsType = typeof(NicknameSync);
        _setNickHub = ReflectionHelpers.CreateInstanceFieldSetter<NicknameSync, ReferenceHub>(nsType, "_hub");
        _setNickFilter = ReflectionHelpers.CreateInstanceFieldSetter<NicknameSync, Regex>(nsType, "_nickFilter");
        _setNickReplacement = ReflectionHelpers.CreateInstanceFieldSetter<NicknameSync, string>(nsType, "_replacement");
        _callSetNick = ReflectionHelpers.CreateOpenDelegate<Action<NicknameSync, string>>(
            AccessTools.Method(nsType, "SetNick", new[] { typeof(string) }));

        _validateComponents = ReflectionHelpers.CreateOpenDelegate<Action<NetworkIdentity>>(
            AccessTools.Method(typeof(NetworkIdentity), "ValidateComponents", Type.EmptyTypes));
        _serializeBehaviour = ReflectionHelpers.CreateOpenDelegate<Action<NetworkBehaviour, NetworkWriter, bool>>(
            AccessTools.Method(typeof(NetworkBehaviour), "Serialize", new[] { typeof(NetworkWriter), typeof(bool) }));

        return _getExchange != null && _setExchange != null && _setEcdhKeys != null &&
               _getEncryptionKey != null && _setEncryptionKey != null && _getCryptoDebug != null && _callServerProcessExchange != null &&
               _getAuthRequested != null && _getChallenge != null && _setChallenge != null && _getTimeoutTimer != null &&
               _getAuthHub != null && _setAuthResponse != null && _setMsgAuthToken != null && _setMsgBadgeToken != null &&
               _rejectAuth != null && _rejectBadgeToken != null && _finalizeAuth != null && _checkBans != null &&
               _getServerRolesHub != null && _getServerRolesPrevBadge != null && _setServerRolesPrevBadge != null &&
               _setServerRolesBgc != null && _setServerRolesBgt != null && _setServerRolesAuthorizeBadge != null &&
               _setServerRolesMyText != null && _getServerRolesBadgeCover != null && _getServerRolesGlobalPerms != null &&
               _setServerRolesGlobalPerms != null &&
               _setNickHub != null && _setNickFilter != null && _setNickReplacement != null && _callSetNick != null &&
               _validateComponents != null && _serializeBehaviour != null;
    }

    private static TDelegate CreateStructPropertySetter<TDelegate>(Type structType, string propName, Type valueType)
        where TDelegate : Delegate
    {
        PropertyInfo prop = AccessTools.Property(structType, propName);
        MethodInfo setter = prop?.GetSetMethod(nonPublic: true);
        if (setter == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_StructSet_{structType.Name}_{propName}",
            typeof(void),
            new[] { structType.MakeByRefType(), valueType },
            structType.Module,
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, setter);
        il.Emit(OpCodes.Ret);
        return (TDelegate)dm.CreateDelegate(typeof(TDelegate));
    }

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        if (!EnsureAccessorsCompiled())
        {
            return MarkIncompatible("PlayerAuthenticationManager / EncryptedChannelManager / ServerRoles / NicknameSync / Mirror internals mismatch.");
        }

        _asyncAuthEnabled = config.AsyncAuthCryptoOffload;
        _ecdhTargetPoolSize = Math.Max(2, Math.Min(32, config.EcdhPreGenPoolSize));
        _maxAuthWorkers = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
        _cachedServerVersion = new System.Version(GameCore.Version.Major, GameCore.Version.Minor, GameCore.Version.Revision);

        _subEcdhPoolActive = false;
        _subAsyncAuthActive = false;
        _subBadgeSkipActive = false;
        _subBanCacheActive = false;
        _subNickRegexActive = false;
        _subSendSpawnActive = false;

        int activeParts = 0;

        // 1. Background ECDH P-384 Keypair Pool
        MethodInfo prepareExchange = AccessTools.Method(typeof(EncryptedChannelManager), "PrepareExchange", Type.EmptyTypes);
        MethodInfo keyToString = AccessTools.Method(typeof(ECDSA), nameof(ECDSA.KeyToString), new[] { typeof(AsymmetricKeyParameter) });
        if (prepareExchange != null && keyToString != null &&
            patchManager.PatchPrefix(Id, prepareExchange, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(PrepareExchangePrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, keyToString, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(KeyToStringPrefix)), guard, config))
        {
            _subEcdhPoolActive = true;
            activeParts++;
        }

        // 2. Async Background Auth Crypto Offload
        MethodInfo processAuth = AccessTools.Method(typeof(PlayerAuthenticationManager), "ProcessAuthenticationResponse", new[] { typeof(AuthenticationResponse) });
        if (_asyncAuthEnabled && processAuth != null &&
            patchManager.PatchPrefix(Id, processAuth, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(ProcessAuthResponsePrefix)), guard, config))
        {
            _subAsyncAuthActive = true;
            activeParts++;
        }

        // 3. Redundant 4th ECDSA Verify Skip in ServerRoles.Update
        MethodInfo srUpdate = AccessTools.Method(typeof(ServerRoles), "Update", Type.EmptyTypes);
        if (srUpdate != null &&
            patchManager.PatchPrefix(Id, srUpdate, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(ServerRolesUpdatePrefix)), guard, config, checkConflict: false))
        {
            _subBadgeSkipActive = true;
            activeParts++;
        }

        // 4. In-Memory Mtime-Cached BanHandler.QueryBan
        MethodInfo queryBan = AccessTools.Method(typeof(BanHandler), nameof(BanHandler.QueryBan), new[] { typeof(string), typeof(string) });
        MethodInfo issueBan = AccessTools.Method(typeof(BanHandler), nameof(BanHandler.IssueBan), new[] { typeof(BanDetails), typeof(BanHandler.BanType), typeof(bool) });
        MethodInfo removeBan = AccessTools.Method(typeof(BanHandler), nameof(BanHandler.RemoveBan), new[] { typeof(string), typeof(BanHandler.BanType), typeof(bool) });
        MethodInfo validateBansTyped = AccessTools.Method(typeof(BanHandler), nameof(BanHandler.ValidateBans), new[] { typeof(BanHandler.BanType) });
        MethodInfo initBans = AccessTools.Method(typeof(BanHandler), nameof(BanHandler.Init), Type.EmptyTypes);
        if (queryBan != null &&
            patchManager.PatchPrefix(Id, queryBan, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(QueryBanPrefix)), guard, config))
        {
            if (issueBan != null)
                patchManager.PatchPostfix(Id, issueBan, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(InvalidateBanCachePostfix)), guard, config);
            if (removeBan != null)
                patchManager.PatchPostfix(Id, removeBan, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(InvalidateBanCachePostfix)), guard, config);
            if (validateBansTyped != null)
                patchManager.PatchPostfix(Id, validateBansTyped, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(InvalidateBanCachePostfix)), guard, config);
            if (initBans != null)
                patchManager.PatchPostfix(Id, initBans, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(InvalidateAllBanCachesPostfix)), guard, config);

            InvalidateAllBanCaches();
            _subBanCacheActive = true;
            activeParts++;
        }

        // 5. Static Cached Regex in NicknameSync.Start
        MethodInfo nickStart = AccessTools.Method(typeof(NicknameSync), "Start", Type.EmptyTypes);
        if (nickStart != null &&
            patchManager.PatchPrefix(Id, nickStart, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(NicknameSyncStartPrefix)), guard, config))
        {
            _subNickRegexActive = true;
            activeParts++;
        }

        // 6. Single-Writer Zero-Copy Mirror.NetworkServer.SendSpawnMessage
        MethodInfo sendSpawn = AccessTools.Method(typeof(NetworkServer), "SendSpawnMessage", new[] { typeof(NetworkIdentity), typeof(NetworkConnection) });
        if (sendSpawn != null &&
            patchManager.PatchPrefix(Id, sendSpawn, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(SendSpawnMessagePrefix)), guard, config))
        {
            _subSendSpawnActive = true;
            activeParts++;
        }

        if (activeParts == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("All OPT-22 join acceleration hooks conflicted with external Harmony patches.");
        }

        // 7. Non-conflicting Real-Player Join Profiler hooks (Prefix/Finalizer, never skips original)
        MethodInfo onServerAddPlayer = AccessTools.Method(typeof(NetworkManager), nameof(NetworkManager.OnServerAddPlayer), new[] { typeof(NetworkConnectionToClient) });
        if (onServerAddPlayer != null)
        {
            patchManager.PatchPrefix(Id, onServerAddPlayer, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(OnServerAddPlayerTimingPrefix)), guard, config, checkConflict: false);
            patchManager.PatchFinalizer(Id, onServerAddPlayer, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(OnServerAddPlayerTimingFinalizer)), guard, config);
        }

        MethodInfo spawnObs = AccessTools.Method(typeof(NetworkServer), "SpawnObserversForConnection", new[] { typeof(NetworkConnectionToClient) });
        if (spawnObs != null)
        {
            patchManager.PatchPrefix(Id, spawnObs, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(SpawnObserversTimingPrefix)), guard, config, checkConflict: false);
            patchManager.PatchFinalizer(Id, spawnObs, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(SpawnObserversTimingFinalizer)), guard, config);
        }

        MethodInfo reqAuth = AccessTools.Method(typeof(PlayerAuthenticationManager), "RequestAuthentication", Type.EmptyTypes);
        if (reqAuth != null)
        {
            patchManager.PatchPrefix(Id, reqAuth, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(RequestAuthTimingPrefix)), guard, config, checkConflict: false);
            patchManager.PatchFinalizer(Id, reqAuth, AccessTools.Method(typeof(PlayerJoinAccelModule), nameof(RequestAuthTimingFinalizer)), guard, config);
        }

        _active = true;
        StaticUnityMethods.OnUpdate += OnUnityUpdate;
        ReferenceHub.OnPlayerRemoved += OnPlayerRemoved;

        // Kick off background ECDH pool warmup immediately so even the first joining player hits cache.
        if (_subEcdhPoolActive)
        {
            TriggerEcdhPoolRefill();
        }

        StatusDetail = $"Active (EcdhPool={(_subEcdhPoolActive ? _ecdhTargetPoolSize.ToString() : "off")}, AsyncAuth={(_subAsyncAuthActive ? $"on/{_maxAuthWorkers}w" : "off")}, BadgeSkip={(_subBadgeSkipActive ? "on" : "off")}, BanCache={(_subBanCacheActive ? "on" : "off")}, NickRegex={(_subNickRegexActive ? "on" : "off")}, FastSpawn={(_subSendSpawnActive ? "on" : "off")})";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _active = false;
        StaticUnityMethods.OnUpdate -= OnUnityUpdate;
        ReferenceHub.OnPlayerRemoved -= OnPlayerRemoved;

        while (EcdhPool.TryDequeue(out _)) { }
        PreformattedPem.Clear();
        while (PendingAuthJobs.TryDequeue(out _)) { }
        while (CompletedAuthJobs.TryDequeue(out _)) { }
        InFlightAuth.Clear();
        ActiveTracesByConnId.Clear();
        _spawnObsActiveConnId = -1;
        _subEcdhPoolActive = false;
        _subAsyncAuthActive = false;
        _subBadgeSkipActive = false;
        _subBanCacheActive = false;
        _subNickRegexActive = false;
        _subSendSpawnActive = false;
    }

    private static void OnPlayerRemoved(ReferenceHub hub)
    {
        if (hub == null)
        {
            return;
        }

        if (hub.authManager != null)
        {
            InFlightAuth.Remove(hub.authManager);
        }

        NetworkConnectionToClient conn = hub.connectionToClient;
        if (conn != null)
        {
            ActiveTracesByConnId.Remove(conn.connectionId);
        }
    }

    // =========================================================================
    // Component 1: Background Pre-Generated ECDH P-384 Keypair Pool
    // =========================================================================

    private static void TriggerEcdhPoolRefill()
    {
        if (!_active || !_subEcdhPoolActive)
        {
            return;
        }

        if (EcdhPool.Count >= _ecdhTargetPoolSize)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _ecdhRefillRunning, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(EcdhRefillWorker, null);
    }

    private static int _bgCryptoPreJitDone;

    private static void EcdhRefillWorker(object _)
    {
        try
        {
            // One-time background JIT warm-up of first-join AES-GCM & SHA-512 code paths (0ms main thread)
            if (Interlocked.Exchange(ref _bgCryptoPreJitDone, 1) == 0)
            {
                try
                {
                    byte[] dummyPlain = new byte[16];
                    byte[] dummyKey = new byte[32];
                    AES.AesGcmEncrypt(dummyPlain, dummyKey, new SecureRandom());
                    Sha.HashToString(Sha.Sha512("slperf_warmup"));
                }
                catch
                {
                    // Best-effort JIT warmup only
                }
            }

            int target = _ecdhTargetPoolSize;
            while (_active && _subEcdhPoolActive && EcdhPool.Count < target)
            {
                AsymmetricCipherKeyPair keys = ECDH.GenerateKeys();
                ECDHBasicAgreement agreement = ECDH.Init(keys);
                string pem = ECDSA.KeyToString(keys.Public);
                EcdhPool.Enqueue(new PreGeneratedEcdh(keys, agreement, pem));
            }
        }
        catch (Exception ex)
        {
            SLPerformanceLog.Warn($"[OPT-22] Background ECDH keypair pre-generation error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ecdhRefillRunning, 0);
            // Re-check once in case pool drained while we were exiting
            if (_active && _subEcdhPoolActive && EcdhPool.Count < _ecdhTargetPoolSize)
            {
                if (Interlocked.CompareExchange(ref _ecdhRefillRunning, 1, 0) == 0)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(EcdhRefillWorker, null);
                }
            }
        }
    }

    private static bool PrepareExchangePrefix(EncryptedChannelManager __instance)
    {
        if (!_active || !_subEcdhPoolActive)
        {
            return true;
        }

        if (_getExchange(__instance) != null)
        {
            return false;
        }

        if (EcdhPool.TryDequeue(out PreGeneratedEcdh entry))
        {
            _setEcdhKeys(__instance, entry.Keys);
            _setExchange(__instance, entry.Agreement);

            if (PreformattedPem.Count > 64)
            {
                PreformattedPem.Clear();
            }

            PreformattedPem[entry.Keys.Public] = entry.PublicPem;
            Interlocked.Increment(ref _ecdhPoolHits);
            _lastPrepareExchangePoolHit = true;
            TriggerEcdhPoolRefill();
            return false;
        }

        Interlocked.Increment(ref _ecdhPoolMisses);
        _lastPrepareExchangePoolHit = false;
        TriggerEcdhPoolRefill();
        return true;
    }

    private static bool KeyToStringPrefix(AsymmetricKeyParameter key, ref string __result)
    {
        if (key != null && PreformattedPem.TryRemove(key, out string cached))
        {
            __result = cached;
            Interlocked.Increment(ref _ecdhPemHits);
            return false;
        }

        return true;
    }

    // =========================================================================
    // Component 2: Async Background Offload for ProcessAuthenticationResponse
    // =========================================================================

    private static bool ProcessAuthResponsePrefix(PlayerAuthenticationManager __instance, AuthenticationResponse msg)
    {
        if (!_active || !_subAsyncAuthActive)
        {
            return true;
        }

        long t0 = Stopwatch.GetTimestamp();
        ReferenceHub hub = _getAuthHub(__instance);
        NetworkConnectionToClient conn = __instance.connectionToClient;
        JoinTrace trace = null;
        if (conn != null && ActiveTracesByConnId.TryGetValue(conn.connectionId, out JoinTrace existingTrace))
        {
            trace = existingTrace;
        }

        try
        {
            if (!_getAuthRequested(__instance))
            {
                hub.gameConsoleTransmission.SendToClient("Authentication token was not requested by the server.", "yellow");
                return false;
            }

            string challenge = _getChallenge(__instance);
            if (challenge == null || InFlightAuth.Contains(__instance))
            {
                hub.gameConsoleTransmission.SendToClient("Authentication token has already been sent.", "yellow");
                return false;
            }

            _setAuthResponse(__instance, msg);

            if (msg.SignedAuthToken == null)
            {
                _rejectAuth(__instance, "authentication token not provided.", null, false);
                return false;
            }

            bool isLocal = __instance.isLocalPlayer;
            if ((msg.EcdhPublicKey == null || msg.EcdhPublicKeySignature == null) && !isLocal)
            {
                _rejectAuth(__instance, "null ECDH public key or public key signature.", null, false);
                return false;
            }

            AsymmetricKeyParameter serverPubKey = ServerConsole.PublicKey;
            if (hub == null || conn == null || serverPubKey == null)
            {
                Interlocked.Increment(ref _asyncAuthFallbackSync);
                return true;
            }

            IPEndPoint remoteEp = null;
            if (!isLocal && LiteNetLib4MirrorServer.Peers != null &&
                LiteNetLib4MirrorServer.Peers.TryGetValue(conn.connectionId, out NetPeer peer))
            {
                remoteEp = peer?.EndPoint;
            }

            ECDHBasicAgreement exchange = hub.encryptedChannelManager != null ? _getExchange(hub.encryptedChannelManager) : null;
            bool needsDerive = msg.EcdhPublicKey != null &&
                               hub.encryptedChannelManager != null &&
                               _getEncryptionKey(hub.encryptedChannelManager) == null &&
                               exchange != null;

            AuthCryptoJob job = new AuthCryptoJob
            {
                AuthManager = __instance,
                Hub = hub,
                Connection = conn,
                Msg = msg,
                Challenge = challenge,
                PlayerId = hub.PlayerId,
                IsLocalPlayer = isLocal,
                RemoteEndPoint = remoteEp,
                ServerPublicKey = serverPubKey,
                IsTestServer = CentralServer.TestServer,
                IsPrivateBeta = GameCore.Version.PrivateBeta,
                ServerVersion = _cachedServerVersion,
                EcdhExchange = exchange,
                NeedsEcdhDerive = needsDerive,
                ActiveTrace = trace,
                EnqueueTimestamp = t0
            };

            InFlightAuth.Add(__instance);
            PendingAuthJobs.Enqueue(job);
            Interlocked.Increment(ref _asyncAuthOffloaded);
            TryPumpAuthWorkers();

            if (trace != null)
            {
                trace.AuthEnqueueTicks = Stopwatch.GetTimestamp() - t0;
                trace.Offloaded = true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // Fail-safe: fall back to exact vanilla handler on unexpected error
            InFlightAuth.Remove(__instance);
            Interlocked.Increment(ref _asyncAuthFallbackSync);
            SLPerformanceLog.Warn($"[OPT-22] Async auth enqueue fallback to sync: {ex.Message}");
            return true;
        }
    }

    private static void TryPumpAuthWorkers()
    {
        while (_active && !PendingAuthJobs.IsEmpty)
        {
            int cur = Volatile.Read(ref _activeAuthWorkers);
            if (cur >= _maxAuthWorkers)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _activeAuthWorkers, cur + 1, cur) == cur)
            {
                ThreadPool.UnsafeQueueUserWorkItem(AuthCryptoWorkerLoop, null);
                return;
            }
        }
    }

    private static void AuthCryptoWorkerLoop(object _)
    {
        try
        {
            while (_active && PendingAuthJobs.TryDequeue(out AuthCryptoJob job))
            {
                RunAuthCryptoJobPure(job);
                CompletedAuthJobs.Enqueue(job);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeAuthWorkers);
            if (_active && !PendingAuthJobs.IsEmpty)
            {
                TryPumpAuthWorkers();
            }
        }
    }

    private static void RunAuthCryptoJobPure(AuthCryptoJob job)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            // Step 1: Verify SignedAuthToken (P-384 ECDSA verify #1 + UTF8Json decode)
            job.AuthOk = TryVerifyTokenPure(
                job.Msg.SignedAuthToken,
                "Authentication",
                job.ServerPublicKey,
                job.IsTestServer,
                out job.AuthToken,
                out job.AuthError,
                out job.AuthUserId,
                out job.AuthCryptoException);

            if (!job.AuthOk || job.AuthToken == null)
            {
                job.EarlyRejectedBeforeEcdh = true;
                return;
            }

            // Cheap pre-checks before running P-384 verify #2 and ECDH derive
            AuthenticationToken tok = job.AuthToken;
            if (job.Challenge != tok.Challenge ||
                tok.PublicKey != job.Msg.PublicKeyHash ||
                (job.IsPrivateBeta && !tok.PrivateBetaOwnership) ||
                tok.PlayerId != job.PlayerId)
            {
                job.EarlyRejectedBeforeEcdh = true;
                return;
            }

            if (tok.GameVersion != null && job.ServerVersion != null)
            {
                System.Version tokVer = System.Version.Parse(tok.GameVersion);
                if (job.ServerVersion > tokVer)
                {
                    job.EarlyRejectedBeforeEcdh = true;
                    return;
                }
            }

            // Step 2: Verify client ECDH public key signature (P-384 ECDSA verify #2)
            if (job.Msg.EcdhPublicKey != null)
            {
                job.EcdhSigOk = VerifySignaturePure(
                    job.Msg.EcdhPublicKey,
                    job.Msg.EcdhPublicKeySignature,
                    job.Msg.PublicKey,
                    out job.EcdhSigException);

                if (!job.EcdhSigOk)
                {
                    return;
                }

                // Step 3: Derive shared AES-GCM key via P-384 ECDH scalar multiplication
                if (job.NeedsEcdhDerive && job.EcdhExchange != null)
                {
                    AsymmetricKeyParameter remoteKey = ECDSA.PublicKeyFromString(job.Msg.EcdhPublicKey);
                    job.DerivedEncryptionKey = ECDH.DeriveKey(job.EcdhExchange, remoteKey);
                }
            }
            else
            {
                job.EcdhSigOk = true;
            }

            // Step 4: Verify SignedBadgeToken if present (P-384 ECDSA verify #3 + UTF8Json decode)
            if (job.Msg.SignedBadgeToken != null)
            {
                job.BadgeChecked = true;
                job.BadgeOk = TryVerifyTokenPure(
                    job.Msg.SignedBadgeToken,
                    "Badge request",
                    job.ServerPublicKey,
                    job.IsTestServer,
                    out job.BadgeToken,
                    out job.BadgeError,
                    out _,
                    out job.BadgeCryptoException);
            }
        }
        catch (Exception ex)
        {
            job.WorkerException = ex;
        }
        finally
        {
            job.WorkerCryptoTicks = Stopwatch.GetTimestamp() - t0;
        }
    }

    internal static bool VerifySignaturePure(string data, byte[] signature, AsymmetricKeyParameter pubKey, out Exception error)
    {
        error = null;
        if (data == null || signature == null || pubKey == null)
        {
            return false;
        }

        try
        {
            ISigner signer = SignerUtilities.GetSigner("SHA-256withECDSA");
            signer.Init(forSigning: false, pubKey);
            byte[] buf = Encoding.UTF8.GetBytes(data);
            signer.BlockUpdate(buf, 0, buf.Length);
            return signer.VerifySignature(signature);
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    internal static bool TryVerifyTokenPure<T>(
        SignedToken signed,
        string usage,
        AsymmetricKeyParameter serverPubKey,
        bool isTestServer,
        out T token,
        out string error,
        out string userId,
        out Exception cryptoException)
        where T : Token, IJsonSerializable
    {
        token = null;
        error = null;
        userId = null;
        cryptoException = null;

        byte[] sigBytes = Convert.FromBase64String(signed.signature);
        if (!VerifySignaturePure(signed.token, sigBytes, serverPubKey, out cryptoException))
        {
            error = "invalid signature of token used for " + usage;
            return false;
        }

        T val = JsonSerialize.FromJson<T>(StringUtils.Base64Decode(signed.token));
        userId = val.UserId;
        if (val.TokenVersion != 2)
        {
            error = $"invalid token version. Expected: {2}, got: {val.TokenVersion}";
            return false;
        }

        if (!val.Usage.Equals(usage, StringComparison.Ordinal))
        {
            error = "invalid token usage. Expected: " + usage + ", got: " + val.Usage;
            return false;
        }

        if (val.ExpirationTime < DateTime.UtcNow)
        {
            error = "expired token used for " + usage;
            return false;
        }

        if (val.IssuanceTime > DateTime.UtcNow.AddMinutes(20.0))
        {
            error = "non-issued token used for " + usage;
            return false;
        }

        if (val.TestSignature && !isTestServer)
        {
            error = "test-only token used for " + usage;
            return false;
        }

        token = val;
        return true;
    }

    private static void OnUnityUpdate()
    {
        if (!_active || CompletedAuthJobs.IsEmpty)
        {
            return;
        }

        long frameBudgetStart = Stopwatch.GetTimestamp();
        int drained = 0;
        while (drained < MaxDrainPerFrame && CompletedAuthJobs.TryDequeue(out AuthCryptoJob job))
        {
            CompleteAuthJobOnMainThread(job);
            drained++;

            if (!CompletedAuthJobs.IsEmpty &&
                (Stopwatch.GetTimestamp() - frameBudgetStart) * TicksToMs >= MaxDrainMsPerFrame)
            {
                break;
            }
        }
    }

    private static void CompleteAuthJobOnMainThread(AuthCryptoJob job)
    {
        PlayerAuthenticationManager authMgr = job.AuthManager;
        if (authMgr != null)
        {
            InFlightAuth.Remove(authMgr);
        }

        ReferenceHub hub = job.Hub;
        NetworkConnectionToClient conn = job.Connection;

        // Fail-closed lifecycle guard: client may have disconnected, timed out, or round restarted mid-crypto
        if (!NetworkServer.active ||
            authMgr == null ||
            hub == null ||
            conn == null ||
            !conn.isReady ||
            _getTimeoutTimer(authMgr) < 0f ||
            !ReferenceHub.AllHubs.Contains(hub) ||
            _getChallenge(authMgr) != job.Challenge)
        {
            Interlocked.Increment(ref _asyncAuthDiscarded);
            if (conn != null)
            {
                ActiveTracesByConnId.Remove(conn.connectionId);
            }
            return;
        }

        JoinTrace trace = job.ActiveTrace;
        if (trace != null)
        {
            trace.AuthWorkerCryptoTicks = job.WorkerCryptoTicks;
            trace.AuthTotalWaitTicks = Stopwatch.GetTimestamp() - job.EnqueueTimestamp;
        }

        _totalSavedWorkerCryptoMs += job.WorkerCryptoTicks * TicksToMs;

        try
        {
            if (job.AuthCryptoException != null)
            {
                GameCore.Console.AddLog(
                    "ECDSA Verification Error (BouncyCastle): " + job.AuthCryptoException.Message + ", " + job.AuthCryptoException.StackTrace,
                    Color.red);
            }

            if (job.WorkerException != null)
            {
                ServerConsole.AddLog("Exception during authentication (client address: " + conn.address + "): " + job.WorkerException.Message, ConsoleColor.Magenta);
                ServerConsole.AddLog(job.WorkerException.StackTrace, ConsoleColor.Magenta);
                _rejectAuth(authMgr, "server exception during authentication!", null, false);
                return;
            }

            AuthenticationResponse msg = job.Msg;

            if (!job.AuthOk || job.AuthToken == null)
            {
                _rejectAuth(authMgr, job.AuthError, job.AuthUserId, true);
                return;
            }

            AuthenticationToken token = job.AuthToken;
            string userId = job.AuthUserId;
            string unsalted = RemoveSalt(token.UserId);

            if (_getChallenge(authMgr) != token.Challenge)
            {
                _rejectAuth(authMgr, "invalid authentication challenge.", userId, false);
                return;
            }

            _setChallenge(authMgr, null);

            if (token.PublicKey != msg.PublicKeyHash)
            {
                _rejectAuth(authMgr, "public key hash mismatch.", userId, false);
                return;
            }

            if (GameCore.Version.PrivateBeta && !token.PrivateBetaOwnership)
            {
                _rejectAuth(authMgr, "you don't own the Private Beta Access Pass DLC.", userId, false);
                return;
            }

            if (token.PlayerId != hub.PlayerId)
            {
                _rejectAuth(authMgr, $"PlayerID Mismatch.\nYour ID: {hub.PlayerId}\nAuth ID: {token.PlayerId}", userId, false);
                return;
            }

            if (token.GameVersion != null)
            {
                System.Version version = System.Version.Parse(token.GameVersion);
                System.Version version2 = _cachedServerVersion ?? new System.Version(GameCore.Version.Major, GameCore.Version.Minor, GameCore.Version.Revision);
                if (version2 > version)
                {
                    _rejectAuth(authMgr, $"Version mismatch\nAuthentication token is only valid until version: {version}\nGame server version: {version2}", userId, false);
                    return;
                }
            }

            IPEndPoint iPEndPoint = null;
            if (!job.IsLocalPlayer)
            {
                if (LiteNetLib4MirrorServer.Peers != null &&
                    LiteNetLib4MirrorServer.Peers.TryGetValue(conn.connectionId, out NetPeer livePeer))
                {
                    iPEndPoint = livePeer?.EndPoint;
                }
                else
                {
                    iPEndPoint = job.RemoteEndPoint;
                }

                if (iPEndPoint != null)
                {
                    bool preauthMatched = CustomLiteNetLib4MirrorTransport.UserIds.TryGetValue(iPEndPoint, out PreauthItem preauthValue) &&
                                          preauthValue.UserId != null &&
                                          preauthValue.UserId.Equals(unsalted, StringComparison.Ordinal);

                    if (!preauthMatched && !CustomLiteNetLib4MirrorTransport.UserIdFastReload.ContainsKey(unsalted))
                    {
                        hub.gameConsoleTransmission.SendToClient("UserID mismatch between authentication and preauthentication token.", "red");
                        hub.gameConsoleTransmission.SendToClient("Preauth: " + (CustomLiteNetLib4MirrorTransport.UserIds.TryGetValue(iPEndPoint, out PreauthItem value2) ? value2.UserId : "(null)"), "red");
                        hub.gameConsoleTransmission.SendToClient("Auth: " + unsalted, "red");
                        _rejectAuth(authMgr, "UserID mismatch between authentication and preauthentication token. Check the game console for more details.", unsalted, false);
                        return;
                    }

                    CustomLiteNetLib4MirrorTransport.UserIds.TryRemove(iPEndPoint, out _);
                }
            }

            if (CustomLiteNetLib4MirrorTransport.UserIdFastReload.ContainsKey(unsalted))
            {
                CustomLiteNetLib4MirrorTransport.UserIdFastReload.TryRemove(unsalted, out _);
            }

            if (job.EcdhSigException != null)
            {
                GameCore.Console.AddLog(
                    "ECDSA Verification Error (BouncyCastle): " + job.EcdhSigException.Message + ", " + job.EcdhSigException.StackTrace,
                    Color.red);
            }

            if (msg.EcdhPublicKey != null && !job.EcdhSigOk)
            {
                _rejectAuth(authMgr, "invalid ECDH exchange public key signature.", userId, false);
                return;
            }

            long tBan = Stopwatch.GetTimestamp();
            bool banPass = _checkBans(authMgr, token, unsalted);
            if (trace != null)
            {
                trace.CheckBansTicks = Stopwatch.GetTimestamp() - tBan;
            }

            if (!banPass)
            {
                return;
            }

            // Apply pre-derived ECDH AES key (zero main-thread P-384 math!)
            if (msg.EcdhPublicKey != null)
            {
                EncryptedChannelManager ecm = hub.encryptedChannelManager;
                if (_getEncryptionKey(ecm) == null)
                {
                    if (job.DerivedEncryptionKey != null)
                    {
                        if (_getCryptoDebug())
                        {
                            ServerConsole.AddLog("Received ECDH parameters from " + hub.LoggedNameFromRefHub() + ".");
                        }
                        _setEncryptionKey(ecm, job.DerivedEncryptionKey);
                    }
                    else
                    {
                        _callServerProcessExchange(ecm, msg.EcdhPublicKey);
                    }
                }
            }

            _setMsgAuthToken(ref msg, token);
            _setAuthResponse(authMgr, msg);

            string authLog = string.Format(
                "{0} authenticated from endpoint {1}. Player ID assigned: {2}. Auth token serial number: {3}.",
                RemoveSalt(msg.AuthToken.UserId),
                iPEndPoint == null ? "(null)" : iPEndPoint.ToString(),
                hub.PlayerId,
                msg.AuthToken.Serial);
            ServerConsole.AddLog(authLog);
            ServerLogs.AddLog(ServerLogs.Modules.Networking, authLog, ServerLogs.ServerLogType.ConnectionUpdate);

            long tFin = Stopwatch.GetTimestamp();
            RunFinalizeAuthenticationInstrumented(authMgr, hub, conn, trace);
            if (trace != null)
            {
                trace.FinalizeAuthTicks = Stopwatch.GetTimestamp() - tFin;
                trace.PlayerId = hub.PlayerId;
                trace.UserId = unsalted;
            }

            // Apply pre-verified BadgeToken if present (exact vanilla lines 403-498)
            long tBadge = Stopwatch.GetTimestamp();
            if (msg.SignedBadgeToken != null)
            {
                if (job.BadgeCryptoException != null)
                {
                    GameCore.Console.AddLog(
                        "ECDSA Verification Error (BouncyCastle): " + job.BadgeCryptoException.Message + ", " + job.BadgeCryptoException.StackTrace,
                        Color.red);
                }

                if (job.BadgeChecked && job.BadgeOk && job.BadgeToken != null)
                {
                    BadgeToken token2 = job.BadgeToken;
                    if (token2.Serial != msg.AuthToken.Serial)
                    {
                        _rejectAuth(authMgr, "token serial number mismatch.", null, false);
                        return;
                    }

                    if (token2.UserId != Sha.HashToString(Sha.Sha512(authMgr.SaltedUserId)))
                    {
                        _rejectBadgeToken(authMgr, "badge token UserID mismatch.");
                        return;
                    }

                    if (StringUtils.Base64Decode(token2.Nickname) != hub.nicknameSync.MyNick)
                    {
                        _rejectBadgeToken(authMgr, "badge token nickname mismatch.");
                        return;
                    }

                    _setMsgBadgeToken(ref msg, token2);
                    _setAuthResponse(authMgr, msg);

                    ulong num = (token2.RaPermissions == 0UL || ServerStatic.PermissionsHandler.NorthwoodAccess)
                        ? ServerStatic.PermissionsHandler.FullPerm
                        : token2.RaPermissions;

                    ulong globalPerms = _getServerRolesGlobalPerms(hub.serverRoles);
                    if ((token2.Management || token2.GlobalBanning) && CustomNetworkManager.IsVerified)
                    {
                        globalPerms |= 8388608UL;
                        globalPerms |= 1048576UL;
                    }

                    if (token2.OverwatchMode)
                    {
                        globalPerms |= 4096UL;
                    }

                    if (token2.Staff || token2.Management || token2.GlobalBanning)
                    {
                        globalPerms |= 16908288UL;
                    }

                    if ((token2.Staff && ServerStatic.PermissionsHandler.NorthwoodAccess) ||
                        (token2.RemoteAdmin && ServerStatic.PermissionsHandler.StaffAccess) ||
                        (token2.Management && ServerStatic.PermissionsHandler.ManagersAccess) ||
                        (token2.GlobalBanning && ServerStatic.PermissionsHandler.BanningTeamAccess))
                    {
                        globalPerms |= num;
                    }

                    _setServerRolesGlobalPerms(hub.serverRoles, globalPerms);

                    if ((token2.BadgeText != null && token2.BadgeText != "(none)") ||
                        (token2.BadgeColor != null && token2.BadgeColor != "(none)"))
                    {
                        if (hub.serverRoles.UserBadgePreferences == ServerRoles.BadgePreferences.PreferGlobal ||
                            !_getServerRolesBadgeCover(hub.serverRoles) ||
                            hub.serverRoles.Group == null)
                        {
                            bool hide = msg.HideBadge;
                            switch (token2.BadgeType)
                            {
                                case 1:
                                    if (ConfigFile.ServerConfig.GetBool("hide_staff_badges_by_default"))
                                        hide = true;
                                    break;
                                case 2:
                                    if (ConfigFile.ServerConfig.GetBool("hide_management_badges_by_default"))
                                        hide = true;
                                    break;
                                case 0:
                                    if (ConfigFile.ServerConfig.GetBool("hide_patreon_badges_by_default") && !CustomNetworkManager.IsVerified)
                                        hide = true;
                                    break;
                                case 3:
                                    hide = true;
                                    break;
                            }

                            if (hide)
                            {
                                hub.serverRoles.HiddenBadge = token2.BadgeText;
                                hub.serverRoles.GlobalHidden = true;
                                hub.serverRoles.RefreshHiddenTag();
                                hub.gameConsoleTransmission.SendToClient("Your global badge has been granted, but it's hidden. Use \".gtag\" command in the game console to show your global badge.", "yellow");
                            }
                            else
                            {
                                hub.serverRoles.HiddenBadge = null;
                                hub.serverRoles.RpcResetFixed();
                                hub.serverRoles.NetworkGlobalBadge = msg.SignedBadgeToken.token;
                                hub.serverRoles.NetworkGlobalBadgeSignature = msg.SignedBadgeToken.signature;
                                hub.gameConsoleTransmission.SendToClient("Your global badge has been granted.", "cyan");
                            }
                        }
                        else
                        {
                            hub.gameConsoleTransmission.SendToClient("Your global badge is covered by server badge. Use \".gtag\" command in the game console to show your global badge.", "yellow");
                        }
                    }

                    hub.serverRoles.FinalizeSetGroup();
                }
                else
                {
                    _rejectBadgeToken(authMgr, job.BadgeError ?? "invalid signature of token used for Badge request");
                }
            }

            if (trace != null)
            {
                trace.BadgeApplyTicks = Stopwatch.GetTimestamp() - tBadge;
            }

            long tJoin = Stopwatch.GetTimestamp();
            InvokeOnJoinedInstrumented(hub, trace);
            if (trace != null)
            {
                trace.OnJoinedTicks = Stopwatch.GetTimestamp() - tJoin;
                trace.Completed = true;
                CommitJoinTrace(conn.connectionId, trace);
            }

            Interlocked.Increment(ref _asyncAuthCompleted);
        }
        catch (Exception ex)
        {
            ServerConsole.AddLog("Exception during authentication (client address: " + conn.address + "): " + ex.Message, ConsoleColor.Magenta);
            ServerConsole.AddLog(ex.StackTrace, ConsoleColor.Magenta);
            _rejectAuth(authMgr, "server exception during authentication!", null, false);
        }
    }

    private static bool HasAnyExternalPatches(MethodBase method)
    {
        if (method == null)
        {
            return true;
        }

        Patches info = Harmony.GetPatchInfo(method);
        if (info == null)
        {
            return false;
        }

        return info.Prefixes.Count > 0 || info.Postfixes.Count > 0 || info.Transpilers.Count > 0 || info.Finalizers.Count > 0;
    }

    private static string FormatDelegateName(Delegate d)
    {
        if (d == null || d.Method == null)
        {
            return "<unknown>";
        }

        MethodInfo m = d.Method;
        string asm = m.DeclaringType?.Assembly?.GetName()?.Name ?? "?";
        string typeName = m.DeclaringType?.Name ?? "?";
        return $"{asm}!{typeName}.{m.Name}";
    }

    private static void RunFinalizeAuthenticationInstrumented(
        PlayerAuthenticationManager authMgr,
        ReferenceHub hub,
        NetworkConnectionToClient conn,
        JoinTrace trace)
    {
        bool canInstrument = trace != null &&
                             _setPrivUserId != null &&
                             _getTargetInstanceMode != null &&
                             _setTargetInstanceMode != null &&
                             _setDoNotTrack != null &&
                             _callRefreshSyncedId != null &&
                             _getAllowSameAccountJoining != null &&
                             _getOnSyncedUserIdAssigned != null &&
                             _getOnInstanceModeChanged != null &&
                             !HasAnyExternalPatches(_finalizeAuthMethod);

        if (!canInstrument)
        {
            _finalizeAuth(authMgr);
            return;
        }

        // Exact line-by-line replica of PlayerAuthenticationManager.FinalizeAuthentication() with per-stage + per-subscriber timing
        StringBuilder slowSubs = null;

        // 1. UserId = AuthenticationResponse.AuthToken.UserId (with per-subscriber OnSyncedUserIdAssigned & OnInstanceModeChanged timing)
        long t0 = Stopwatch.GetTimestamp();
        string rawUserId = authMgr.AuthenticationResponse.AuthToken.UserId;
        _setPrivUserId(authMgr, rawUserId);

        Action<ReferenceHub> syncedEvent = _getOnSyncedUserIdAssigned();
        if (syncedEvent != null)
        {
            Delegate[] list = syncedEvent.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                long ts = Stopwatch.GetTimestamp();
                ((Action<ReferenceHub>)list[i])(hub);
                double ms = (Stopwatch.GetTimestamp() - ts) * TicksToMs;
                if (ms >= 0.5)
                {
                    slowSubs ??= new StringBuilder(128);
                    if (slowSubs.Length > 0) slowSubs.Append(", ");
                    slowSubs.Append("Uid:").Append(FormatDelegateName(list[i])).Append('=').Append(ms.ToString("F2")).Append("ms");
                }
            }
        }

        ClientInstanceMode newMode;
        if (string.IsNullOrEmpty(rawUserId))
        {
            newMode = ClientInstanceMode.Unverified;
        }
        else if (rawUserId == "ID_Dedicated")
        {
            newMode = ClientInstanceMode.DedicatedServer;
        }
        else if (rawUserId == "ID_Host")
        {
            newMode = ClientInstanceMode.Host;
        }
        else if (rawUserId == "ID_Dummy")
        {
            newMode = ClientInstanceMode.Dummy;
        }
        else
        {
            newMode = ClientInstanceMode.ReadyClient;
        }

        long tModeStart = Stopwatch.GetTimestamp();
        if (newMode != _getTargetInstanceMode(authMgr))
        {
            _setTargetInstanceMode(authMgr, newMode);
            Action<ReferenceHub, ClientInstanceMode> modeEvent = _getOnInstanceModeChanged();
            if (modeEvent != null)
            {
                Delegate[] list = modeEvent.GetInvocationList();
                for (int i = 0; i < list.Length; i++)
                {
                    long ts = Stopwatch.GetTimestamp();
                    ((Action<ReferenceHub, ClientInstanceMode>)list[i])(hub, newMode);
                    double ms = (Stopwatch.GetTimestamp() - ts) * TicksToMs;
                    if (ms >= 0.5)
                    {
                        slowSubs ??= new StringBuilder(128);
                        if (slowSubs.Length > 0) slowSubs.Append(", ");
                        slowSubs.Append("Mode:").Append(FormatDelegateName(list[i])).Append('=').Append(ms.ToString("F2")).Append("ms");
                    }
                }
            }
        }

        trace.FinalizeModeTicks = Stopwatch.GetTimestamp() - tModeStart;
        _callRefreshSyncedId(authMgr);
        hub.serverRoles.RefreshRealId();
        trace.FinalizeUserIdTicks = (Stopwatch.GetTimestamp() - t0) - trace.FinalizeModeTicks;

        // 2. DoNotTrack & UpdateNickname
        long tNick = Stopwatch.GetTimestamp();
        bool dnt = authMgr.AuthenticationResponse.DoNotTrack || authMgr.AuthenticationResponse.AuthToken.DoNotTrack;
        _setDoNotTrack(authMgr, dnt);
        hub.nicknameSync.UpdateNickname(StringUtils.Base64Decode(authMgr.AuthenticationResponse.AuthToken.Nickname));
        if (dnt)
        {
            ServerLogs.AddLog(
                ServerLogs.Modules.Networking,
                hub.LoggedNameFromRefHub() + " connected from IP address " + conn.address + " sent Do Not Track signal.",
                ServerLogs.ServerLogType.ConnectionUpdate);
        }
        trace.FinalizeNickTicks = Stopwatch.GetTimestamp() - tNick;

        // 3. Welcome encrypted console message
        long tWelcome = Stopwatch.GetTimestamp();
        hub.gameConsoleTransmission.SendToClient("Hi " + hub.nicknameSync.MyNick + "! You have been authenticated on this server.", "green");
        trace.FinalizeWelcomeTicks = Stopwatch.GetTimestamp() - tWelcome;

        // 4. RefreshPermissions + duplicate account kick check
        long tPerms = Stopwatch.GetTimestamp();
        hub.serverRoles.RefreshPermissions();
        if (!_getAllowSameAccountJoining())
        {
            int playerId = hub.PlayerId;
            string currentUid = authMgr.UserId;
            foreach (ReferenceHub allHub in ReferenceHub.AllHubs)
            {
                if (allHub.authManager.UserId == currentUid && allHub.PlayerId != playerId && !allHub.isLocalPlayer)
                {
                    ServerConsole.AddLog($"Player {currentUid} ({allHub.PlayerId}, {conn.address}) has been kicked from the server, because he has just joined the server again from IP address {conn.address}.");
                    ServerConsole.Disconnect(allHub.gameObject, "Only one player instance of the same player is allowed.");
                }
            }
        }
        trace.FinalizePermsTicks = Stopwatch.GetTimestamp() - tPerms;
        trace.SlowFinalizeBreakdown = slowSubs != null ? slowSubs.ToString() : null;
    }

    private static void InvokeOnJoinedInstrumented(ReferenceHub hub, JoinTrace trace)
    {
        PlayerJoinedEventArgs ev = new PlayerJoinedEventArgs(hub);
        if (trace == null || _getPlayerJoinedEvent == null || HasAnyExternalPatches(_onJoinedMethod))
        {
            PlayerEvents.OnJoined(ev);
            return;
        }

        LabEventHandler<PlayerJoinedEventArgs> joinedHandler = _getPlayerJoinedEvent();
        if (joinedHandler == null)
        {
            return;
        }

        StringBuilder slowSubs = null;
        Delegate[] list = joinedHandler.GetInvocationList();
        for (int i = 0; i < list.Length; i++)
        {
            Delegate d = list[i];
            long ts = Stopwatch.GetTimestamp();
            try
            {
                if (d is LabEventHandler<PlayerJoinedEventArgs> h)
                {
                    h(ev);
                }
            }
            catch (Exception ex)
            {
                LabApi.Features.Console.Logger.Error(EventManager.FormatErrorMessage(joinedHandler, ex));
            }

            double ms = (Stopwatch.GetTimestamp() - ts) * TicksToMs;
            if (ms >= 0.5)
            {
                slowSubs ??= new StringBuilder(160);
                if (slowSubs.Length > 0) slowSubs.Append(", ");
                slowSubs.Append(FormatDelegateName(d)).Append('=').Append(ms.ToString("F2")).Append("ms");
            }
        }

        trace.SlowOnJoinedBreakdown = slowSubs != null ? slowSubs.ToString() : null;
    }

    private static string RemoveSalt(string userId)
    {
        if (userId == null)
        {
            return null;
        }

        int idx = userId.IndexOf('$');
        return idx >= 0 ? userId.Substring(0, idx) : userId;
    }

    // =========================================================================
    // Component 3: Skip Redundant 4th P-384 ECDSA Verify in ServerRoles.Update
    // =========================================================================

    private static bool ServerRolesUpdatePrefix(ServerRoles __instance)
    {
        if (!_active || !_subBadgeSkipActive || !NetworkServer.active)
        {
            return true;
        }

        // Fast 2-pointer check: 99.99% of frames exit here in ~2 ns
        string gb = __instance.GlobalBadge;
        if (gb == _getServerRolesPrevBadge(__instance) || string.IsNullOrEmpty(gb))
        {
            return true;
        }

        // Only short-circuit when FixedBadge branch won't return early in vanilla Update()
        if (!string.IsNullOrEmpty(__instance.FixedBadge))
        {
            return true;
        }

        ReferenceHub hub = _getServerRolesHub(__instance);
        if (hub == null || hub.authManager == null)
        {
            return true;
        }

        AuthenticationResponse resp = hub.authManager.AuthenticationResponse;
        BadgeToken token = resp.BadgeToken;
        SignedToken signed = resp.SignedBadgeToken;
        if (token == null || signed == null ||
            !string.Equals(gb, signed.token, StringComparison.Ordinal) ||
            !string.Equals(__instance.GlobalBadgeSignature, signed.signature, StringComparison.Ordinal))
        {
            return true;
        }

        // Exact vanilla ServerRoles.Update lines 764-814 without re-running BouncyCastle P-384 ECDSA verify!
        string saltedId = hub.authManager.SaltedUserId;
        if (token.UserId != saltedId && token.UserId != Sha.HashToString(Sha.Sha512(saltedId)))
        {
            return true; // Let vanilla handle failure logging/reset identically
        }

        if (StringUtils.Base64Decode(token.Nickname) != hub.nicknameSync.MyNick)
        {
            return true;
        }

        _setServerRolesPrevBadge(__instance, gb);
        string bt = token.BadgeText;
        string bc = token.BadgeColor;
        if ((bt == null || bt == "(none)") && (bc == null || bc == "(none)"))
        {
            _setServerRolesBgc(__instance, null);
            _setServerRolesBgt(__instance, null);
            _setServerRolesAuthorizeBadge(__instance, false);
        }
        else
        {
            _setServerRolesBgc(__instance, bc);
            __instance.SetColor(bc);
            _setServerRolesBgt(__instance, bt);
            _setServerRolesMyText(__instance, bt);
            _setServerRolesAuthorizeBadge(__instance, true);
        }

        Interlocked.Increment(ref _globalBadgeReverifySkips);
        // Let vanilla lines 817-843 run normally (_prevColor / _prevText / PlayerList.UpdatePlayerRole)
        return true;
    }

    // =========================================================================
    // Component 4: Lock-Free Mtime-Cached BanHandler.QueryBan
    // =========================================================================

    private static void InvalidateAllBanCaches()
    {
        Interlocked.Exchange(ref UserIdBanBucket.ForceReload, 1);
        Interlocked.Exchange(ref IpBanBucket.ForceReload, 1);
    }

    private static void InvalidateAllBanCachesPostfix()
    {
        InvalidateAllBanCaches();
    }

    private static void InvalidateBanCachePostfix(BanHandler.BanType banType)
    {
        if (banType == BanHandler.BanType.UserId)
        {
            Interlocked.Exchange(ref UserIdBanBucket.ForceReload, 1);
        }
        else if (banType == BanHandler.BanType.IP)
        {
            Interlocked.Exchange(ref IpBanBucket.ForceReload, 1);
        }
        else
        {
            InvalidateAllBanCaches();
        }
    }

    internal static BanDetails LookupBanInBucket(BanCacheBucket bucket, string path, string sanitizedId)
    {
        BanTableSnapshot snap = GetOrRefreshBanSnapshot(bucket, path);
        if (snap == null)
        {
            return null;
        }

        if (snap.ById.TryGetValue(sanitizedId, out BanDetails found))
        {
            return found;
        }

        return null;
    }

    internal static BanTableSnapshot GetOrRefreshBanSnapshot(BanCacheBucket bucket, string path)
    {
        long now = Stopwatch.GetTimestamp();
        BanTableSnapshot snap = Volatile.Read(ref bucket.Snapshot);

        bool needsCheck = snap == null ||
                          !string.Equals(snap.Path, path, StringComparison.Ordinal) ||
                          Volatile.Read(ref bucket.ForceReload) != 0 ||
                          (now - Volatile.Read(ref bucket.LastStatTicks)) >= OneSecondTicks;

        if (!needsCheck)
        {
            Interlocked.Increment(ref _banCacheHits);
            return snap;
        }

        lock (bucket.ReloadLock)
        {
            snap = bucket.Snapshot;
            now = Stopwatch.GetTimestamp();
            bool force = Interlocked.Exchange(ref bucket.ForceReload, 0) != 0;
            bool pathChanged = snap == null || !string.Equals(snap.Path, path, StringComparison.Ordinal);

            if (!force && !pathChanged && (now - bucket.LastStatTicks) < OneSecondTicks)
            {
                Interlocked.Increment(ref _banCacheHits);
                return snap;
            }

            FileInfo fi = new FileInfo(path);
            if (!fi.Exists)
            {
                bucket.LastStatTicks = now;
                if (snap == null || pathChanged || snap.ById.Count != 0)
                {
                    snap = new BanTableSnapshot(path, DateTime.MinValue, 0L, new Dictionary<string, BanDetails>(0, StringComparer.Ordinal));
                    Volatile.Write(ref bucket.Snapshot, snap);
                    Interlocked.Increment(ref _banCacheReloads);
                }
                else
                {
                    Interlocked.Increment(ref _banCacheHits);
                }

                return snap;
            }

            DateTime mtime = fi.LastWriteTimeUtc;
            long len = fi.Length;
            bucket.LastStatTicks = now;

            if (!force && !pathChanged && snap != null && snap.LastWriteUtc == mtime && snap.FileLength == len)
            {
                Interlocked.Increment(ref _banCacheHits);
                return snap;
            }

            Dictionary<string, BanDetails> dict = new Dictionary<string, BanDetails>(StringComparer.Ordinal);
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    BanDetails item = BanHandler.ProcessBanItem(line, bucket.Type);
                    if (item != null && !string.IsNullOrEmpty(item.Id) && !dict.ContainsKey(item.Id))
                    {
                        dict.Add(item.Id, item);
                    }
                }
            }

            snap = new BanTableSnapshot(path, mtime, len, dict);
            Volatile.Write(ref bucket.Snapshot, snap);
            Interlocked.Increment(ref _banCacheReloads);
            return snap;
        }
    }

    private static string SanitizeBanId(string id)
    {
        if (id.IndexOf(';') < 0 && id.IndexOf('\n') < 0 && id.IndexOf('\r') < 0)
        {
            return id;
        }

        return id.Replace(";", ":").Replace(Environment.NewLine, string.Empty).Replace("\n", string.Empty);
    }

    private static bool QueryBanPrefix(string userId, string ip, ref KeyValuePair<BanDetails, BanDetails> __result)
    {
        if (!_active || !_subBanCacheActive)
        {
            return true;
        }

        try
        {
            BanDetails userBan = null;
            BanDetails ipBan = null;

            if (!string.IsNullOrEmpty(userId))
            {
                string cleanUserId = SanitizeBanId(userId);
                BanDetails raw = LookupBanInBucket(UserIdBanBucket, BanHandler.GetPath(BanHandler.BanType.UserId), cleanUserId);
                if (raw != null)
                {
                    userBan = BanHandler.ReturnChecks(raw, BanHandler.BanType.UserId);
                }
            }

            if (!string.IsNullOrEmpty(ip))
            {
                string cleanIp = SanitizeBanId(ip);
                BanDetails raw = LookupBanInBucket(IpBanBucket, BanHandler.GetPath(BanHandler.BanType.IP), cleanIp);
                if (raw != null)
                {
                    ipBan = BanHandler.ReturnChecks(raw, BanHandler.BanType.IP);
                }
            }

            __result = new KeyValuePair<BanDetails, BanDetails>(userBan, ipBan);
            return false;
        }
        catch
        {
            return true;
        }
    }

    // =========================================================================
    // Component 5: Static Cached Regex in NicknameSync.Start
    // =========================================================================

    private static bool NicknameSyncStartPrefix(NicknameSync __instance)
    {
        if (!_active || !_subNickRegexActive)
        {
            return true;
        }

        _setNickHub(__instance, ReferenceHub.GetHub(__instance.gameObject));
        _setNickFilter(__instance, null);
        _setNickReplacement(__instance, string.Empty);

        if (NetworkServer.active)
        {
            __instance.NetworkViewRange = ConfigFile.ServerConfig.GetFloat("player_info_range", 10f);
            string pattern = ConfigFile.ServerConfig.GetString("nickname_filter") ?? string.Empty;
            if (!string.IsNullOrEmpty(pattern))
            {
                Regex regex;
                if (_cachedNickFilterRegex != null && string.Equals(_cachedNickFilterPattern, pattern, StringComparison.Ordinal))
                {
                    regex = _cachedNickFilterRegex;
                    Interlocked.Increment(ref _nickRegexCacheHits);
                }
                else
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500.0));
                    _cachedNickFilterPattern = pattern;
                    _cachedNickFilterRegex = regex;
                    Interlocked.Increment(ref _nickRegexCompiledCount);
                }

                _setNickFilter(__instance, regex);
                _setNickReplacement(__instance, ConfigFile.ServerConfig.GetString("nickname_filter_replacement") ?? string.Empty);
            }
        }

        if (__instance.isLocalPlayer)
        {
            _callSetNick(__instance, "Dedicated Server");
        }

        return false;
    }

    // =========================================================================
    // Component 6: Single-Writer Zero-Copy Mirror.NetworkServer.SendSpawnMessage
    // =========================================================================

    private static bool SendSpawnMessagePrefix(NetworkIdentity identity, NetworkConnection conn)
    {
        if (!_active || !_subSendSpawnActive)
        {
            return true;
        }

        if (identity.serverOnly)
        {
            return false;
        }

        if (_spawnObsActiveConnId == conn.connectionId)
        {
            _spawnObsCounter++;
        }

        bool isOwner = identity.connectionToClient == conn;
        using NetworkWriterPooled payloadWriter = NetworkWriterPool.Get();
        ArraySegment<byte> payload = BuildSingleWriterInitialPayload(isOwner, identity, payloadWriter);

        SpawnMessage message = new SpawnMessage
        {
            netId = identity.netId,
            isLocalPlayer = conn.identity == identity,
            isOwner = isOwner,
            sceneId = identity.sceneId,
            assetId = identity.assetId,
            position = identity.transform.localPosition,
            rotation = identity.transform.localRotation,
            scale = identity.transform.localScale,
            payload = payload
        };

        conn.Send(message);
        _sendSpawnFastCalls++;
        return false;
    }

    internal static ArraySegment<byte> BuildSingleWriterInitialPayload(bool isOwner, NetworkIdentity identity, NetworkWriter writer)
    {
        NetworkBehaviour[] behaviours = identity.NetworkBehaviours;
        if (behaviours == null || behaviours.Length == 0)
        {
            return default;
        }

        _validateComponents(identity);
        behaviours = identity.NetworkBehaviours;
        int len = behaviours.Length;
        if (len == 0)
        {
            return default;
        }

        ulong mask = 0UL;
        if (isOwner)
        {
            for (int i = 0; i < len; i++)
            {
                mask |= (uint)(1 << i);
            }

            if (mask == 0UL)
            {
                return default;
            }

            Compression.CompressVarUInt(writer, mask);
            for (int i = 0; i < len; i++)
            {
                _serializeBehaviour(behaviours[i], writer, true);
            }
        }
        else
        {
            for (int i = 0; i < len; i++)
            {
                if (behaviours[i].syncMode == SyncMode.Observers)
                {
                    mask |= (uint)(1 << i);
                }
            }

            if (mask == 0UL)
            {
                return default;
            }

            Compression.CompressVarUInt(writer, mask);
            for (int i = 0; i < len; i++)
            {
                NetworkBehaviour nb = behaviours[i];
                if (nb.syncMode == SyncMode.Observers)
                {
                    _serializeBehaviour(nb, writer, true);
                }
            }
        }

        return writer.ToArraySegment();
    }

    // =========================================================================
    // Component 7: Non-Conflicting Real-Player Join Profiler (`slperf joinstats`)
    // =========================================================================

    private static bool IsRealClientConnection(NetworkConnectionToClient conn)
    {
        return conn != null &&
               !(conn is LocalConnectionToClient) &&
               !(conn is DummyNetworkConnection);
    }

    private static JoinTrace GetOrCreateTrace(NetworkConnectionToClient conn)
    {
        if (!IsRealClientConnection(conn))
        {
            return null;
        }

        if (!ActiveTracesByConnId.TryGetValue(conn.connectionId, out JoinTrace trace))
        {
            if (ActiveTracesByConnId.Count >= 64)
            {
                ActiveTracesByConnId.Clear();
            }

            trace = new JoinTrace
            {
                TimestampUtc = DateTime.UtcNow,
                ConnectionId = conn.connectionId
            };
            ActiveTracesByConnId[conn.connectionId] = trace;
        }

        return trace;
    }

    private static void CommitJoinTrace(int connId, JoinTrace trace)
    {
        ActiveTracesByConnId.Remove(connId);
        if (trace == null)
        {
            return;
        }

        JoinHistory[_joinHistoryHead] = trace;
        _joinHistoryHead = (_joinHistoryHead + 1) % JoinHistoryCapacity;
        if (_joinHistoryCount < JoinHistoryCapacity)
        {
            _joinHistoryCount++;
        }
    }

    private static void OnServerAddPlayerTimingPrefix(NetworkConnectionToClient conn, out long __state)
    {
        if (!_active || !IsRealClientConnection(conn))
        {
            __state = 0;
            return;
        }

        GetOrCreateTrace(conn);
        __state = Stopwatch.GetTimestamp();
    }

    private static void OnServerAddPlayerTimingFinalizer(NetworkConnectionToClient conn, long __state)
    {
        if (__state == 0 || conn == null)
        {
            return;
        }

        if (ActiveTracesByConnId.TryGetValue(conn.connectionId, out JoinTrace trace))
        {
            trace.AddPlayerTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void SpawnObserversTimingPrefix(NetworkConnectionToClient conn, out long __state)
    {
        if (!_active || !IsRealClientConnection(conn) || !conn.isReady)
        {
            __state = 0;
            return;
        }

        GetOrCreateTrace(conn);
        _spawnObsActiveConnId = conn.connectionId;
        _spawnObsCounter = 0;
        __state = Stopwatch.GetTimestamp();
    }

    private static void SpawnObserversTimingFinalizer(NetworkConnectionToClient conn, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        long elapsed = Stopwatch.GetTimestamp() - __state;
        int spawnedCount = _spawnObsCounter;
        _spawnObsActiveConnId = -1;
        _spawnObsCounter = 0;

        if (conn != null && ActiveTracesByConnId.TryGetValue(conn.connectionId, out JoinTrace trace))
        {
            trace.SpawnObsTicks += elapsed;
            trace.SpawnObsCount += spawnedCount;
        }
    }

    private static void RequestAuthTimingPrefix(PlayerAuthenticationManager __instance, out long __state)
    {
        if (!_active || __instance.isLocalPlayer || !IsRealClientConnection(__instance.connectionToClient))
        {
            __state = 0;
            return;
        }

        _lastPrepareExchangePoolHit = false;
        __state = Stopwatch.GetTimestamp();
    }

    private static void RequestAuthTimingFinalizer(PlayerAuthenticationManager __instance, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        NetworkConnectionToClient conn = __instance.connectionToClient;
        if (conn != null && ActiveTracesByConnId.TryGetValue(conn.connectionId, out JoinTrace trace))
        {
            trace.ReqAuthTicks += Stopwatch.GetTimestamp() - __state;
            trace.EcdhPoolHit = _lastPrepareExchangePoolHit;
        }
    }

    public static string FormatJoinStatsReport()
    {
        StringBuilder sb = new StringBuilder(1536);
        sb.AppendLine("=== SLPerformance OPT-22 Player Join & Auth Spike Report (`slperf joinstats`) ===");
        sb.AppendLine($"Status          : {(_active ? "ACTIVE" : "INACTIVE")} | Workers={_maxAuthWorkers} | InFlightAuth={InFlightAuth.Count} | Pending={PendingAuthJobs.Count} | CompletedQueue={CompletedAuthJobs.Count}");
        sb.AppendLine($"Sub-Hooks       : EcdhPool={(_subEcdhPoolActive ? "on" : "off")} | AsyncAuth={(_subAsyncAuthActive ? "on" : "off")} | BadgeSkip={(_subBadgeSkipActive ? "on" : "off")} | BanCache={(_subBanCacheActive ? "on" : "off")} | NickRegex={(_subNickRegexActive ? "on" : "off")} | FastSendSpawn={(_subSendSpawnActive ? "on" : "off")}");
        sb.AppendLine($"ECDH P-384 Pool : Ready={EcdhPool.Count}/{_ecdhTargetPoolSize} | PoolHits={Volatile.Read(ref _ecdhPoolHits)} | Misses={Volatile.Read(ref _ecdhPoolMisses)} | PemHits={Volatile.Read(ref _ecdhPemHits)}");
        sb.AppendLine($"Async Auth      : Offloaded={Volatile.Read(ref _asyncAuthOffloaded)} | Completed={Volatile.Read(ref _asyncAuthCompleted)} | Discarded={Volatile.Read(ref _asyncAuthDiscarded)} | SyncFallback={Volatile.Read(ref _asyncAuthFallbackSync)} | SavedMainThreadCrypto={_totalSavedWorkerCryptoMs:F2}ms");
        sb.AppendLine($"Aux Accel       : GlobalBadgeReverifySkips={Volatile.Read(ref _globalBadgeReverifySkips)} | BanCacheHits={Volatile.Read(ref _banCacheHits)} (Reloads={Volatile.Read(ref _banCacheReloads)}) | NickRegexHits={Volatile.Read(ref _nickRegexCacheHits)} (Compiled={Volatile.Read(ref _nickRegexCompiledCount)}) | FastSendSpawn={_sendSpawnFastCalls}");

        if (_joinHistoryCount == 0)
        {
            sb.AppendLine("Recent Real-Player Joins: (none recorded yet; join with a real client to capture exact ms breakdown)");
            return sb.ToString();
        }

        sb.AppendLine($"Recent Real-Player Joins (last {_joinHistoryCount}):");
        for (int i = 0; i < _joinHistoryCount; i++)
        {
            int idx = (_joinHistoryHead - 1 - i + JoinHistoryCapacity) % JoinHistoryCapacity;
            JoinTrace t = JoinHistory[idx];
            if (t == null)
            {
                continue;
            }

            double addMs = t.AddPlayerTicks * TicksToMs;
            double obsMs = t.SpawnObsTicks * TicksToMs;
            double reqMs = t.ReqAuthTicks * TicksToMs;
            double enqMs = t.AuthEnqueueTicks * TicksToMs;
            double wrkMs = t.AuthWorkerCryptoTicks * TicksToMs;
            double waitMs = t.AuthTotalWaitTicks * TicksToMs;
            double banMs = t.CheckBansTicks * TicksToMs;
            double finMs = t.FinalizeAuthTicks * TicksToMs;
            double bdgMs = t.BadgeApplyTicks * TicksToMs;
            double onJoinMs = t.OnJoinedTicks * TicksToMs;
            double finishMainMs = banMs + finMs + bdgMs + onJoinMs;

            sb.AppendLine(
                $"  [{t.TimestampUtc:HH:mm:ss}] pid={t.PlayerId} conn={t.ConnectionId} | " +
                $"AddPlayer={addMs:F2}ms (SpawnObs={obsMs:F2}ms/{t.SpawnObsCount}obj) | " +
                $"ReqAuth={reqMs:F2}ms (ECDH={(t.EcdhPoolHit ? "HIT" : "MISS")})");
            sb.AppendLine(
                $"             AuthEnqueue={enqMs:F2}ms | OffloadedCrypto={wrkMs:F2}ms (Wait={waitMs:F1}ms) | " +
                $"AuthFinishMain={finishMainMs:F2}ms [Bans={banMs:F2}, Finalize={finMs:F2}, Badge={bdgMs:F2}, OnJoined={onJoinMs:F2}]");
            double fUidMs = t.FinalizeUserIdTicks * TicksToMs;
            double fModeMs = t.FinalizeModeTicks * TicksToMs;
            double fNickMs = t.FinalizeNickTicks * TicksToMs;
            double fWelcMs = t.FinalizeWelcomeTicks * TicksToMs;
            double fPermMs = t.FinalizePermsTicks * TicksToMs;
            if (fUidMs + fModeMs + fNickMs + fWelcMs + fPermMs > 0.0)
            {
                sb.AppendLine(
                    $"             FinalizeDetail : Uid={fUidMs:F2}ms, ModeEvents={fModeMs:F2}ms, Nick={fNickMs:F2}ms, WelcomeAes={fWelcMs:F2}ms, Perms={fPermMs:F2}ms");
            }
            if (!string.IsNullOrEmpty(t.SlowFinalizeBreakdown))
            {
                sb.AppendLine($"             SlowFinalize   : {t.SlowFinalizeBreakdown}");
            }
            if (!string.IsNullOrEmpty(t.SlowOnJoinedBreakdown))
            {
                sb.AppendLine($"             SlowOnJoined   : {t.SlowOnJoinedBreakdown}");
            }
        }

        return sb.ToString();
    }
}
