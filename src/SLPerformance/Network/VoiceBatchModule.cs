using System;
using System.Reflection;
using HarmonyLib;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using Mirror;
using PlayerRoles.Voice;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using VoiceChat;
using VoiceChat.Networking;

namespace SLPerformance.Network;

/// <summary>
/// OPT-05: Optimizes VoiceTransceiver.ServerReceiveMessage:
/// - Skips PlayerSendingVoiceMessageEventArgs and PlayerReceivingVoiceMessageEventArgs allocations when unsubscribed
/// - Packs identical VoiceMessage payload ONCE per distinct output VoiceChatChannel instead of re-packing per listener
/// </summary>
public sealed class VoiceBatchModule : OptimizationModuleBase
{
    public override string Id => "net.voice_batch_and_lazy_args";
    public override string Name => "VoiceTransceiver Per-Channel Single Pack & Lazy Events";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Avoids allocating Voice EventArgs when unsubscribed and packs outgoing VoiceMessage once per output channel.";

    private const int ChannelSlots = 16;

    private static Func<bool> _hasSendingSubscribers;
    private static Func<bool> _hasReceivingSubscribers;
    private static Func<VoiceTransceiver.VoiceMessageReceiving> _getLegacyReceivingEvent;
    private static Action<VoiceModuleBase, VoiceChatChannel> _setCurrentChannel;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.NetVoiceBatchAndLazyArgs;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        MethodInfo target = AccessTools.Method(
            typeof(VoiceTransceiver),
            "ServerReceiveMessage",
            new[] { typeof(NetworkConnection), typeof(VoiceMessage) });

        _hasSendingSubscribers = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), "SendingVoiceMessage");
        _hasReceivingSubscribers = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), "ReceivingVoiceMessage");
        _getLegacyReceivingEvent = ReflectionHelpers.CreateStaticFieldGetter<VoiceTransceiver.VoiceMessageReceiving>(typeof(VoiceTransceiver), "OnVoiceMessageReceiving");
        _setCurrentChannel = ReflectionHelpers.CreateOpenDelegate<Action<VoiceModuleBase, VoiceChatChannel>>(
            AccessTools.PropertySetter(typeof(VoiceModuleBase), nameof(VoiceModuleBase.CurrentChannel)));

        if (target == null || _hasSendingSubscribers == null || _hasReceivingSubscribers == null ||
            _getLegacyReceivingEvent == null || _setCurrentChannel == null)
        {
            return MarkIncompatible("VoiceTransceiver or PlayerEvents voice fields not found.");
        }

        if (!patchManager.PatchPrefix(Id, target, AccessTools.Method(typeof(VoiceBatchModule), nameof(ServerReceiveMessagePrefix)), guard, config))
        {
            return MarkConflict("External Harmony patch detected on VoiceTransceiver.ServerReceiveMessage.");
        }

        StatusDetail = "Active (lazy EventArgs + single-pack per channel)";
        return true;
    }

    private static bool ServerReceiveMessagePrefix(NetworkConnection conn, VoiceMessage msg)
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            if (msg.SpeakerNull ||
                conn.identity == null ||
                msg.Speaker.netId != conn.identity.netId ||
                msg.Speaker.roleManager.CurrentRole is not IVoiceRole voiceRole ||
                !voiceRole.VoiceModule.CheckRateLimit() ||
                VoiceChatMutes.IsMuted(msg.Speaker))
            {
                return false;
            }

            VoiceChatChannel validatedSendChannel = voiceRole.VoiceModule.ValidateSend(msg.Channel);
            if (validatedSendChannel == VoiceChatChannel.None)
            {
                return false;
            }

            _setCurrentChannel(voiceRole.VoiceModule, validatedSendChannel);

            if (_hasSendingSubscribers())
            {
                PlayerSendingVoiceMessageEventArgs sendEv = new PlayerSendingVoiceMessageEventArgs(ref msg);
                PlayerEvents.OnSendingVoiceMessage(sendEv);
                if (!sendEv.IsAllowed)
                {
                    return false;
                }
                msg = sendEv.Message;
            }

            bool hasReceivingSubs = _hasReceivingSubscribers();
            VoiceTransceiver.VoiceMessageReceiving legacyEvent = _getLegacyReceivingEvent();

            // Cache packed writers indexed by (byte)VoiceChatChannel for identical base payloads.
            NetworkWriterPooled[] packedByChannel = null;
            int maxSize = NetworkMessages.MaxMessageSize(0);

            ReferenceHub baseSpeaker = msg.Speaker;
            byte[] baseData = msg.Data;
            int baseLen = msg.DataLength;

            try
            {
                foreach (ReferenceHub listener in ReferenceHub.AllHubs)
                {
                    if (listener.roleManager.CurrentRole is not IVoiceRole listenerVoiceRole)
                    {
                        continue;
                    }

                    VoiceChatChannel recvChannel = listenerVoiceRole.VoiceModule.ValidateReceive(msg.Speaker, validatedSendChannel);
                    VoiceMessage outMsg = msg;
                    outMsg.Channel = recvChannel;

                    if (hasReceivingSubs)
                    {
                        PlayerReceivingVoiceMessageEventArgs recvEv = new PlayerReceivingVoiceMessageEventArgs(listener, ref outMsg);
                        PlayerEvents.OnReceivingVoiceMessage(recvEv);
                        if (!recvEv.IsAllowed)
                        {
                            continue;
                        }
                        outMsg = recvEv.Message;
                    }

                    if (outMsg.Channel == VoiceChatChannel.None)
                    {
                        continue;
                    }

                    legacyEvent?.Invoke(outMsg, listener);

                    NetworkConnectionToClient targetConn = listener.connectionToClient;
                    if (targetConn == null)
                    {
                        continue;
                    }

                    // Reuse single packed buffer if payload & speaker match the base message and channel fits slot table
                    int chIdx = (int)outMsg.Channel;
                    bool canReuse = chIdx >= 0 && chIdx < ChannelSlots &&
                                    ReferenceEquals(outMsg.Speaker, baseSpeaker) &&
                                    ReferenceEquals(outMsg.Data, baseData) &&
                                    outMsg.DataLength == baseLen;

                    if (canReuse)
                    {
                        packedByChannel ??= new NetworkWriterPooled[ChannelSlots];
                        NetworkWriterPooled cached = packedByChannel[chIdx];
                        if (cached == null)
                        {
                            cached = NetworkWriterPool.Get();
                            NetworkMessages.Pack(outMsg, cached);
                            packedByChannel[chIdx] = cached;
                        }

                        if (cached.Position <= maxSize)
                        {
                            targetConn.Send(cached.ToArraySegment(), 0);
                        }
                    }
                    else
                    {
                        targetConn.Send(outMsg);
                    }
                }
            }
            finally
            {
                if (packedByChannel != null)
                {
                    for (int i = 0; i < ChannelSlots; i++)
                    {
                        if (packedByChannel[i] != null)
                        {
                            NetworkWriterPool.Return(packedByChannel[i]);
                            packedByChannel[i] = null;
                        }
                    }
                }
            }

            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.VoiceReceive, t0);
        }
    }
}
