from __future__ import annotations

from pathlib import Path

import pandas as pd


def write_optional_plots(condition_metrics_df: pd.DataFrame, out_dir: str | Path) -> list[Path]:
    try:
        import matplotlib.pyplot as plt
    except ImportError:
        return []

    if condition_metrics_df.empty:
        return []

    out_path = Path(out_dir)
    out_path.mkdir(parents=True, exist_ok=True)
    plots = [
        ("duration_mean_s", "Duration by condition", "duration_by_condition.png"),
        ("deposited_boxes_mean", "Deposited boxes by condition", "deposited_boxes_by_condition.png"),
        ("voice_events_mean", "Voice events by condition", "voice_events_by_condition.png"),
        ("autonomy_requests_mean", "Autonomy requests by condition", "autonomy_requests_by_condition.png"),
    ]
    written: list[Path] = []
    labels = condition_metrics_df["condition_id"].fillna("unknown").astype(str)
    for column, title, filename in plots:
        if column not in condition_metrics_df.columns:
            continue
        fig, ax = plt.subplots(figsize=(7, 4))
        ax.bar(labels, pd.to_numeric(condition_metrics_df[column], errors="coerce").fillna(0))
        ax.set_title(title)
        ax.set_xlabel("Condition")
        ax.set_ylabel(column)
        ax.tick_params(axis="x", labelrotation=20)
        fig.tight_layout()
        target = out_path / filename
        fig.savefig(target, dpi=140)
        plt.close(fig)
        written.append(target)
    return written
