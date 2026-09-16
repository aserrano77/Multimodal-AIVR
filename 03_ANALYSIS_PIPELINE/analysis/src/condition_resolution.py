from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import pandas as pd


@dataclass(frozen=True)
class CanonicalCondition:
    condition_id: str
    condition_name: str
    robot_enabled: bool
    voice_enabled: bool
    assistance_mode: str


@dataclass
class ConditionResolution:
    condition_id: str = ""
    condition_name: str = ""
    robot_enabled: bool | None = None
    voice_enabled: bool | None = None
    assistance_mode: str = ""
    source: str = "unresolved"
    warnings: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)
    raw_condition_id: str = ""
    raw_condition_log_context: str = ""
    raw_log_context: str = ""
    raw_robot_enabled: Any = None
    raw_voice_enabled: Any = None
    raw_manifest_condition_id: str = ""
    raw_manifest_condition_log_context: str = ""
    raw_manifest_robot_enabled: Any = None
    raw_manifest_voice_enabled: Any = None

    @property
    def resolved(self) -> bool:
        return self.condition_id in CANONICAL_CONDITIONS


CANONICAL_CONDITIONS: dict[str, CanonicalCondition] = {
    "C00_robot_off_voice_off": CanonicalCondition(
        "C00_robot_off_voice_off",
        "Robot OFF + Voice OFF",
        False,
        False,
        "Disabled",
    ),
    "C10_robot_on_voice_off": CanonicalCondition(
        "C10_robot_on_voice_off",
        "Robot ON + Voice OFF",
        True,
        False,
        "AssistedSelection",
    ),
    "C11_robot_on_voice_on": CanonicalCondition(
        "C11_robot_on_voice_on",
        "Robot ON + Voice ON",
        True,
        True,
        "AssistedSelection",
    ),
}


LEGACY_CONTEXT_MAP: dict[str, str] = {
    "c00": "C00_robot_off_voice_off",
    "robot_off_voice_off": "C00_robot_off_voice_off",
    "manual_only": "C00_robot_off_voice_off",
    "no_robot_no_voice": "C00_robot_off_voice_off",
    "c10": "C10_robot_on_voice_off",
    "robot_on_voice_off": "C10_robot_on_voice_off",
    "autonomy_only": "C10_robot_on_voice_off",
    "robot_only": "C10_robot_on_voice_off",
    "voice_only": "C10_robot_on_voice_off",
    "c11": "C11_robot_on_voice_on",
    "robot_on_voice_on": "C11_robot_on_voice_on",
    "multimodal": "C11_robot_on_voice_on",
}


def resolve_condition_from_row(
    row: Any,
    manifest: dict[str, Any] | None = None,
    run_dir: str | Path | None = None,
) -> ConditionResolution:
    manifest = manifest or {}
    values = _row_values(row)
    row_condition_id = _first_text(values, "condition_id", "payload_condition_id")
    row_condition_log_context = _first_text(values, "condition_log_context", "payload_condition_log_context")
    row_robot_enabled = _first_value(values, "robot_enabled", "payload_robot_enabled")
    row_voice_enabled = _first_value(values, "voice_enabled", "payload_voice_enabled")
    row_canonical = canonicalize_condition_token(row_condition_id) or canonicalize_condition_token(row_condition_log_context)
    manifest_map_condition = _condition_from_manifest_maps(manifest, row_canonical)
    multicondition_manifest = _is_multicondition_manifest(manifest)
    candidates: list[tuple[str, str, bool]] = [
        ("condition_id", row_condition_id, False),
        ("condition_log_context", row_condition_log_context, False),
        ("flags", _condition_from_flags(row_robot_enabled, row_voice_enabled), False),
        ("manifest.condition_maps", manifest_map_condition, False),
        ("legacy.log_context", _first_text(values, "log_context", "payload_log_context"), True),
    ]
    if not multicondition_manifest:
        candidates.extend([
            ("manifest.condition_id", _text(manifest.get("condition_id")), False),
            ("manifest.condition_log_context", _text(manifest.get("condition_log_context")), False),
            ("manifest.flags", _condition_from_flags(manifest.get("robot_enabled"), manifest.get("voice_enabled")), False),
            ("manifest.legacy.log_context", _text(manifest.get("log_context")), True),
        ])
    candidates.append(("folder_name", Path(run_dir).name if run_dir else "", True))

    resolution = ConditionResolution(
        raw_condition_id=row_condition_id,
        raw_condition_log_context=row_condition_log_context,
        raw_log_context=_first_text(values, "log_context", "payload_log_context"),
        raw_robot_enabled=row_robot_enabled,
        raw_voice_enabled=row_voice_enabled,
        raw_manifest_condition_id=_text(manifest.get("condition_id")),
        raw_manifest_condition_log_context=_text(manifest.get("condition_log_context")),
        raw_manifest_robot_enabled=manifest.get("robot_enabled"),
        raw_manifest_voice_enabled=manifest.get("voice_enabled"),
    )

    resolved_token = ""
    source = "unresolved"
    used_legacy = False
    for candidate_source, token, legacy in candidates:
        canonical = canonicalize_condition_token(token, allow_legacy=legacy)
        if canonical:
            resolved_token = canonical
            source = candidate_source
            used_legacy = legacy
            break

    if not resolved_token:
        resolution.errors.append("condition_unresolved")
        return resolution

    canonical = CANONICAL_CONDITIONS[resolved_token]
    resolution.condition_id = canonical.condition_id
    resolution.condition_name = canonical.condition_name
    resolution.robot_enabled = canonical.robot_enabled
    resolution.voice_enabled = canonical.voice_enabled
    resolution.assistance_mode = canonical.assistance_mode
    resolution.source = source

    _check_explicit_conflict(resolution, values, manifest, row_canonical)
    if used_legacy:
        resolution.warnings.append(f"legacy_condition_resolution_used:{source}")
    if resolution.raw_log_context and resolution.raw_log_context != resolved_token:
        legacy_canonical = canonicalize_condition_token(resolution.raw_log_context, allow_legacy=True)
        if legacy_canonical and legacy_canonical != resolved_token:
            resolution.warnings.append(f"legacy_log_context_condition_mismatch:{resolution.raw_log_context}->{resolved_token}")
        elif resolution.raw_log_context == "voice_only" and resolved_token == "C10_robot_on_voice_off":
            resolution.warnings.append("legacy_log_context_voice_only_treated_as_c10")
    return resolution


def apply_condition_resolution_to_frame(
    df: pd.DataFrame,
    manifest: dict[str, Any] | None,
    run_dir: str | Path | None,
) -> pd.DataFrame:
    if df.empty:
        return df
    out = df.copy()
    resolutions = [resolve_condition_from_row(row, manifest=manifest, run_dir=run_dir) for _, row in out.iterrows()]
    out["raw_condition_id"] = [item.raw_condition_id for item in resolutions]
    out["raw_condition_log_context"] = [item.raw_condition_log_context for item in resolutions]
    out["raw_log_context"] = [item.raw_log_context for item in resolutions]
    out["raw_robot_enabled"] = [item.raw_robot_enabled for item in resolutions]
    out["raw_voice_enabled"] = [item.raw_voice_enabled for item in resolutions]
    out["raw_manifest_condition_id"] = [item.raw_manifest_condition_id for item in resolutions]
    out["raw_manifest_condition_log_context"] = [item.raw_manifest_condition_log_context for item in resolutions]
    out["raw_manifest_robot_enabled"] = [item.raw_manifest_robot_enabled for item in resolutions]
    out["raw_manifest_voice_enabled"] = [item.raw_manifest_voice_enabled for item in resolutions]
    out["condition_id"] = [item.condition_id or pd.NA for item in resolutions]
    out["condition_name"] = [item.condition_name or pd.NA for item in resolutions]
    out["robot_enabled"] = [item.robot_enabled if item.robot_enabled is not None else pd.NA for item in resolutions]
    out["voice_enabled"] = [item.voice_enabled if item.voice_enabled is not None else pd.NA for item in resolutions]
    out["assistance_mode"] = [item.assistance_mode or pd.NA for item in resolutions]
    out["condition_resolution_source"] = [item.source for item in resolutions]
    out["condition_resolution_warnings"] = ["; ".join(item.warnings) for item in resolutions]
    out["condition_resolution_errors"] = ["; ".join(item.errors) for item in resolutions]
    return out


def canonicalize_condition_token(value: Any, allow_legacy: bool = False) -> str:
    text = _text(value)
    if not text:
        return ""
    if text in CANONICAL_CONDITIONS:
        return text
    lowered = text.lower()
    for condition_id in CANONICAL_CONDITIONS:
        if lowered.startswith(condition_id[:3].lower()):
            return condition_id
    if allow_legacy:
        for token, condition_id in LEGACY_CONTEXT_MAP.items():
            if token in lowered:
                return condition_id
    return ""


def _check_explicit_conflict(
    resolution: ConditionResolution,
    values: dict[str, Any],
    manifest: dict[str, Any],
    row_canonical: str,
) -> None:
    flag_sources = [
        ("row", _first_value(values, "robot_enabled", "payload_robot_enabled"), _first_value(values, "voice_enabled", "payload_voice_enabled")),
    ]
    if _is_multicondition_manifest(manifest) and row_canonical:
        flag_sources.append((
            "manifest.condition_maps",
            _manifest_condition_map_value(manifest, "robot_enabled_by_condition", resolution.condition_id),
            _manifest_condition_map_value(manifest, "voice_enabled_by_condition", resolution.condition_id),
        ))
    else:
        flag_sources.append(("manifest", manifest.get("robot_enabled"), manifest.get("voice_enabled")))

    for label, robot_value, voice_value in flag_sources:
        robot = _as_bool(robot_value)
        voice = _as_bool(voice_value)
        if robot is not None and robot != resolution.robot_enabled:
            resolution.errors.append(f"condition_flag_conflict:{label}.robot_enabled")
        if voice is not None and voice != resolution.voice_enabled:
            resolution.errors.append(f"condition_flag_conflict:{label}.voice_enabled")

    for label, token in [
        ("condition_id", resolution.raw_condition_id),
        ("condition_log_context", resolution.raw_condition_log_context),
    ]:
        canonical = canonicalize_condition_token(token)
        if canonical and canonical != resolution.condition_id:
            resolution.errors.append(f"condition_id_conflict:{label}:{token}->{resolution.condition_id}")


def _condition_from_manifest_maps(manifest: dict[str, Any], condition_id: str) -> str:
    if not condition_id:
        return ""
    return _condition_from_flags(
        _manifest_condition_map_value(manifest, "robot_enabled_by_condition", condition_id),
        _manifest_condition_map_value(manifest, "voice_enabled_by_condition", condition_id),
    )


def _manifest_condition_map_value(manifest: dict[str, Any], key: str, condition_id: str) -> Any:
    values = manifest.get(key)
    if not isinstance(values, dict) or not condition_id:
        return None
    if condition_id in values:
        return values[condition_id]
    lowered = condition_id.lower()
    for candidate, value in values.items():
        if str(candidate).strip().lower() == lowered:
            return value
    return None


def _is_multicondition_manifest(manifest: dict[str, Any]) -> bool:
    order = manifest.get("condition_order_ids") or manifest.get("condition_order") or []
    if isinstance(order, str):
        order = [token.strip() for token in order.replace("->", ">").split(">") if token.strip()]
    if not isinstance(order, (list, tuple)):
        return False
    canonical = {canonicalize_condition_token(token) for token in order}
    canonical.discard("")
    return len(canonical) > 1


def _condition_from_flags(robot_value: Any, voice_value: Any) -> str:
    robot = _as_bool(robot_value)
    voice = _as_bool(voice_value)
    if robot is None or voice is None:
        return ""
    for condition in CANONICAL_CONDITIONS.values():
        if condition.robot_enabled == robot and condition.voice_enabled == voice:
            return condition.condition_id
    return ""


def _row_values(row: Any) -> dict[str, Any]:
    if row is None:
        return {}
    if isinstance(row, dict):
        return row
    if hasattr(row, "to_dict"):
        return row.to_dict()
    return {}


def _first_text(values: dict[str, Any], *keys: str, fallback: str = "") -> str:
    for key in keys:
        value = _text(values.get(key))
        if value:
            return value
    return fallback


def _first_value(values: dict[str, Any], *keys: str) -> Any:
    for key in keys:
        if key in values and not _is_missing(values[key]):
            return values[key]
    return None


def _text(value: Any) -> str:
    if _is_missing(value):
        return ""
    return str(value).strip()


def _is_missing(value: Any) -> bool:
    if value is None:
        return True
    try:
        if pd.isna(value):
            return True
    except (TypeError, ValueError):
        return False
    return False


def _as_bool(value: Any) -> bool | None:
    if _is_missing(value):
        return None
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if text in {"true", "1", "yes", "y"}:
        return True
    if text in {"false", "0", "no", "n"}:
        return False
    return None
