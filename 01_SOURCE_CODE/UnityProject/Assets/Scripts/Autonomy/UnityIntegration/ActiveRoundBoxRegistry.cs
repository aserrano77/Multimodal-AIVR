using System;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    public static class ActiveRoundBoxRegistry
    {
        private static readonly Dictionary<string, GameObject> Boxes =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static int _ownerInstanceId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsForPlayerRun()
        {
            Boxes.Clear();
            _ownerInstanceId = 0;
        }

        public static void BeginOwner(int ownerInstanceId)
        {
            Boxes.Clear();
            _ownerInstanceId = ownerInstanceId;
        }

        public static bool Register(int ownerInstanceId, string boxId, GameObject box)
        {
            if (ownerInstanceId == 0 || ownerInstanceId != _ownerInstanceId ||
                string.IsNullOrWhiteSpace(boxId) || box == null)
            {
                return false;
            }

            Boxes[boxId] = box;
            return true;
        }

        public static void ClearRound(int ownerInstanceId)
        {
            if (ownerInstanceId == _ownerInstanceId)
            {
                Boxes.Clear();
            }
        }

        public static void ReleaseOwner(int ownerInstanceId)
        {
            if (ownerInstanceId != _ownerInstanceId)
            {
                return;
            }

            Boxes.Clear();
            _ownerInstanceId = 0;
        }

        public static bool TryResolve(string boxId, out GameObject box)
        {
            box = null;
            return !string.IsNullOrWhiteSpace(boxId) &&
                   Boxes.TryGetValue(boxId, out box) &&
                   box != null;
        }
    }
}
