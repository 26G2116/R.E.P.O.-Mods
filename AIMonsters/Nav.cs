using UnityEngine;
using UnityEngine.AI;

namespace AIMonsters
{
    /// <summary>敵を歩かせる先を決める共通の処理。</summary>
    internal static class Nav
    {
        /// <summary>相手の背後 distance(m)くらいで、歩ける地点。</summary>
        internal static bool BehindPoint(PlayerAvatar player, float distance, out Vector3 point)
        {
            var vision = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform : player.transform;
            Vector3 back = -Vector3.ProjectOnPlane(vision.forward, Vector3.up).normalized;
            if (back.sqrMagnitude < 0.01f)
            {
                back = -player.transform.forward;
            }
            return Sample(player.transform.position + back * distance, out point);
        }

        /// <summary>一番近いプレイヤーから離れる向きに distance(m)くらいで、歩ける地点。</summary>
        internal static bool AwayPoint(Vector3 from, float distance, out Vector3 point)
        {
            var nearest = NearestPlayer(from);
            Vector3 away = nearest ? Vector3.ProjectOnPlane(from - nearest.transform.position, Vector3.up).normalized : Vector3.zero;
            if (away.sqrMagnitude < 0.01f)
            {
                away = Random.insideUnitSphere;
                away.y = 0f;
                away.Normalize();
            }
            return Sample(from + away * distance, out point);
        }

        internal static PlayerAvatar NearestPlayer(Vector3 from)
        {
            PlayerAvatar nearest = null;
            float best = float.MaxValue;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled)
                {
                    continue;
                }
                float d = Vector3.Distance(from, player.transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = player;
                }
            }
            return nearest;
        }

        internal static bool Sample(Vector3 desired, out Vector3 point)
        {
            if (NavMesh.SamplePosition(desired, out var hit, 3f, NavMesh.AllAreas))
            {
                point = hit.position;
                return true;
            }
            point = desired;
            return false;
        }

        /// <summary>水平の距離。</summary>
        internal static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>from から to が見通せるか(壁・扉などの蝶番の物で遮られていない)。</summary>
        internal static bool LineOfSight(Vector3 from, Vector3 to) =>
            !Physics.Linecast(from, to, LayerMask.GetMask("Default", "PhysGrabObjectHinge"), QueryTriggerInteraction.Ignore);

        internal static Vector3 Eyes(PlayerAvatar player) =>
            player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position + Vector3.up;
    }
}
