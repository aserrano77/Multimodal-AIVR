using System;
using UnityEngine;
using UnityEngine.UI;

namespace Autonomy.UnityIntegration
{
    public enum ExperimentButtonRole
    {
        Primary,
        Secondary,
        Warning,
        Destructive
    }

    public static class ExperimentRuntimeButtonUtility
    {
        public const float ColorMultiplier = 1f;
        public const float FadeDuration = 0.10f;

        public static readonly Color DisabledColor = new Color32(0x3F, 0x44, 0x4B, 0xA6);

        public static ColorBlock BuildColorBlock(ExperimentButtonRole role)
        {
            Color normal;
            Color highlighted;
            Color pressed;
            switch (role)
            {
                case ExperimentButtonRole.Primary:
                    normal = new Color32(0x00, 0x6E, 0xA6, 0xFF);
                    highlighted = new Color32(0x00, 0x77, 0xB6, 0xFF);
                    pressed = new Color32(0x00, 0x5B, 0x8C, 0xFF);
                    break;
                case ExperimentButtonRole.Secondary:
                    normal = new Color32(0x4B, 0x55, 0x63, 0xFF);
                    highlighted = new Color32(0x5B, 0x64, 0x70, 0xFF);
                    pressed = new Color32(0x37, 0x41, 0x51, 0xFF);
                    break;
                case ExperimentButtonRole.Warning:
                    normal = new Color32(0xA8, 0x57, 0x00, 0xFF);
                    highlighted = new Color32(0xB8, 0x5C, 0x00, 0xFF);
                    pressed = new Color32(0x7D, 0x42, 0x00, 0xFF);
                    break;
                case ExperimentButtonRole.Destructive:
                    normal = new Color32(0xB3, 0x26, 0x4A, 0xFF);
                    highlighted = new Color32(0xC1, 0x2C, 0x51, 0xFF);
                    pressed = new Color32(0x8F, 0x1E, 0x3C, 0xFF);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported experiment button role.");
            }

            ColorBlock colors = ColorBlock.defaultColorBlock;
            colors.normalColor = normal;
            colors.highlightedColor = highlighted;
            colors.pressedColor = pressed;
            colors.selectedColor = highlighted;
            colors.disabledColor = DisabledColor;
            colors.colorMultiplier = ColorMultiplier;
            colors.fadeDuration = FadeDuration;
            return colors;
        }

        public static int ConfigureRuntimeButton(
            Button button,
            Image image,
            Action action,
            ExperimentButtonRole role,
            bool enabled)
        {
            if (button == null)
            {
                return 0;
            }

            button.transition = Selectable.Transition.ColorTint;
            button.targetGraphic = image;
            button.colors = BuildColorBlock(role);
            button.interactable = enabled;
            button.onClick.RemoveAllListeners();
            if (action != null)
            {
                button.onClick.AddListener(() => action.Invoke());
            }

            if (image != null)
            {
                image.color = Color.white;
                image.raycastTarget = true;
            }

            return action != null ? 1 : 0;
        }
    }
}
