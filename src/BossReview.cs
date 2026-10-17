using System;
using System.Reflection;
using HarmonyLib;
using Kolkata.UI;
using ScamWYF.Modding.Core;

namespace ScamWYF.AiBackend
{
    /// <summary>
    /// Gives the end-of-day boss review longer than the 30 seconds the game allows it.
    /// </summary>
    /// <remarks>
    /// The game does not wait for this review the way it waits for a caller turn. It starts a timer
    /// before the request and abandons the whole thing when it expires:
    ///
    ///     // ReviewWhiteboardController.m_hostAiTimeoutSeconds, a serialized 30f
    ///     ReviewBossAiClient.TryBuildBossReviewAsync(overview, endpoint, model,
    ///         m_hostAiTimeoutSeconds, cancellationToken);
    ///
    ///     // ReviewBossAiClient.TryBuildWithConversationProviderAsync
    ///     timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(Mathf.Max(1f, timeoutSeconds)));
    ///
    /// That cancellation token reaches this mod's router, which drops the in-flight request and tells
    /// the game the request was cancelled. The game logs "Host OpenRouter review timed out", discards
    /// the review and carries on with the day's results - so a slow review costs the whole narration
    /// and leaves nothing in the log to say why.
    ///
    /// TimeoutSeconds cannot fix this. That is our own timeout; the game's fires first and is the one
    /// that matters. The field is written from Awake, which runs after Unity has deserialized the
    /// scene value, so a prefab that ships 30 does not put it back.
    /// </remarks>
    internal static class BossReview
    {
        private const string TimeoutFieldName = "m_hostAiTimeoutSeconds";

        private static FieldInfo _timeoutField;
        private static object _live;
        private static float _original;
        private static bool _looked;

        /// <summary>
        /// Patch the board's Awake so the deadline is raised before anything asks for a review. Returns
        /// false if the game has moved the field, which leaves the game exactly as it was.
        /// </summary>
        public static bool TryInstall(ScamMod owner, Config cfg)
        {
            if (!ResolveField())
            {
                owner.ModLog.LogWarning(
                    "ReviewWhiteboardController." + TimeoutFieldName + " is gone; the boss review keeps " +
                    "the game's own 30 second deadline. Nothing else is affected.");
                return false;
            }

            // Checked against DeclaringType below, because an inherited Awake would run this for every
            // component in the game rather than just the review board.
            var awake = AccessTools.Method(typeof(ReviewWhiteboardController), "Awake", Type.EmptyTypes);

            if (awake == null || awake.DeclaringType != typeof(ReviewWhiteboardController))
            {
                owner.ModLog.LogWarning(
                    "ReviewWhiteboardController.Awake could not be resolved; the boss review keeps the " +
                    "game's own 30 second deadline.");
                return false;
            }

            return PatchCoordinator.TryPatch(owner, awake,
                "give the boss review " + DescribeTimeout(cfg) + " instead of 30s",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BossReview), nameof(AfterAwake))));
        }

        /// <summary>Re-apply after a config reload, to the board that is already in the scene.</summary>
        public static void Reapply(Config cfg, BepInEx.Logging.ManualLogSource log)
        {
            if (_live == null || !Write(_live, cfg)) return;
            log.LogInfo("Boss review deadline is now " + DescribeTimeout(cfg) + ".");
        }

        // __instance is left untyped on purpose: the board derives from Mirror's NetworkBehaviour, and
        // naming that here would mean this mod's build depending on Mirror.dll to describe one field.
        private static void AfterAwake(object __instance)
        {
            _live = __instance;

            // Remember what the scene asked for, so a config of 0 can put it back rather than leave
            // whatever we last wrote in place.
            try
            {
                if (_timeoutField != null)
                    _original = Convert.ToSingle(_timeoutField.GetValue(__instance));
            }
            catch (Exception)
            {
                // Not being able to read it is not worth failing over; 0 then means "leave it alone".
            }

            if (Plugin.Cfg != null) Write(__instance, Plugin.Cfg);
        }

        private static bool Write(object board, Config cfg)
        {
            if (_timeoutField == null || board == null) return false;

            try
            {
                var current = Convert.ToSingle(_timeoutField.GetValue(board));
                var wanted = cfg.BossReviewTimeoutSeconds.Value > 0
                    ? cfg.BossReviewTimeoutSeconds.Value
                    : _original;

                if (Math.Abs(current - wanted) < 0.5f) return false;

                _timeoutField.SetValue(board, wanted);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not raise the boss review deadline: " + ex.Message);
                return false;
            }
        }

        private static bool ResolveField()
        {
            if (_looked) return _timeoutField != null;
            _looked = true;

            _timeoutField = AccessTools.Field(typeof(ReviewWhiteboardController), TimeoutFieldName);
            if (_timeoutField != null && _timeoutField.FieldType != typeof(float))
            {
                Plugin.Log.LogWarning(TimeoutFieldName + " is a " + _timeoutField.FieldType.Name +
                                      " now, not a float; leaving the game's deadline alone.");
                _timeoutField = null;
            }
            return _timeoutField != null;
        }

        private static string DescribeTimeout(Config cfg)
        {
            var seconds = cfg.BossReviewTimeoutSeconds.Value;
            return seconds > 0 ? seconds + "s" : "the game's own 30s";
        }
    }
}