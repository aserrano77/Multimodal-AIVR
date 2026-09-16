using System;
using System.Collections.Generic;
using UnityEngine.XR;

namespace Autonomy.UnityIntegration
{
    public readonly struct ExperimentPauseInputDeviceSnapshot
    {
        public ExperimentPauseInputDeviceSnapshot(
            string name,
            InputDeviceCharacteristics characteristics,
            bool isValid,
            bool isTracked,
            bool secondaryButtonSupported,
            bool secondaryButtonPressed,
            bool primaryButtonSupported,
            bool primaryButtonPressed,
            bool menuButtonSupported,
            bool menuButtonPressed)
        {
            Name = name ?? string.Empty;
            Characteristics = characteristics;
            IsValid = isValid;
            IsTracked = isTracked;
            SecondaryButtonSupported = secondaryButtonSupported;
            SecondaryButtonPressed = secondaryButtonPressed;
            PrimaryButtonSupported = primaryButtonSupported;
            PrimaryButtonPressed = primaryButtonPressed;
            MenuButtonSupported = menuButtonSupported;
            MenuButtonPressed = menuButtonPressed;
        }

        public string Name { get; }
        public InputDeviceCharacteristics Characteristics { get; }
        public bool IsValid { get; }
        public bool IsTracked { get; }
        public bool SecondaryButtonSupported { get; }
        public bool SecondaryButtonPressed { get; }
        public bool PrimaryButtonSupported { get; }
        public bool PrimaryButtonPressed { get; }
        public bool MenuButtonSupported { get; }
        public bool MenuButtonPressed { get; }
    }

    public readonly struct ExperimentPauseInputDeviceSelection
    {
        public ExperimentPauseInputDeviceSelection(bool found, ExperimentPauseInputDeviceSnapshot device, int index, int score)
        {
            Found = found;
            Device = device;
            Index = index;
            Score = score;
        }

        public bool Found { get; }
        public ExperimentPauseInputDeviceSnapshot Device { get; }
        public int Index { get; }
        public int Score { get; }
    }

    public static class ExperimentPauseInputDeviceResolver
    {
        public static ExperimentPauseInputDeviceSelection SelectBestLeftController(IReadOnlyList<ExperimentPauseInputDeviceSnapshot> devices)
        {
            if (devices == null || devices.Count == 0)
            {
                return default;
            }

            int bestIndex = -1;
            int bestScore = int.MinValue;
            ExperimentPauseInputDeviceSnapshot best = default;
            for (int i = 0; i < devices.Count; i++)
            {
                ExperimentPauseInputDeviceSnapshot device = devices[i];
                if (!IsEligiblePhysicalLeftController(device))
                {
                    continue;
                }

                int score = Score(device);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                    best = device;
                }
            }

            return bestIndex >= 0
                ? new ExperimentPauseInputDeviceSelection(true, best, bestIndex, bestScore)
                : default;
        }

        public static bool IsClearlyHandOrPseudoDevice(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }

            string name = deviceName.ToLowerInvariant();
            return name.Contains("hand interaction") ||
                name.Contains("handtracking") ||
                name.Contains("hand tracking") ||
                name.Contains("hand interaction poses") ||
                name.Contains("palm pose interaction");
        }

        private static bool IsEligiblePhysicalLeftController(ExperimentPauseInputDeviceSnapshot device)
        {
            if (!device.IsValid || IsClearlyHandOrPseudoDevice(device.Name))
            {
                return false;
            }

            bool left = Has(device.Characteristics, InputDeviceCharacteristics.Left);
            bool controller = Has(device.Characteristics, InputDeviceCharacteristics.Controller);
            bool held = Has(device.Characteristics, InputDeviceCharacteristics.HeldInHand);
            return left && controller && held;
        }

        private static int Score(ExperimentPauseInputDeviceSnapshot device)
        {
            int score = 0;
            if (device.SecondaryButtonSupported)
            {
                score += 100;
            }

            if (device.IsTracked)
            {
                score += 20;
            }

            string name = device.Name.ToLowerInvariant();
            if (name.Contains("oculus touch controller openxr"))
            {
                score += 80;
            }
            else if (name.Contains("touch") && name.Contains("controller"))
            {
                score += 50;
            }
            else if (name.Contains("controller"))
            {
                score += 25;
            }

            if (name.Contains("openxr"))
            {
                score += 10;
            }

            return score;
        }

        private static bool Has(InputDeviceCharacteristics value, InputDeviceCharacteristics flag)
        {
            return (value & flag) == flag;
        }
    }
}
