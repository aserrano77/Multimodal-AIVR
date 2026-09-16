"""Validation of the ``order_checksum_v1`` questionnaire code.

The questionnaire code links a Quest session with its KoboToolbox submission and
encodes the experimental order the participant actually ran. Its structure is::

    [sequence][random][random][random][random][checksum]

* Character 1: a letter A-F that encodes one of the six permutations of C00, C10
  and C11.
* Characters 2-5: pseudorandom component, drawn from the alphabet below.
* Character 6: control character computed from the first five.

The same specification is implemented in three other places and all four must
agree: the Unity generator (``QuestionnaireCodeGenerator``), the deployed
XLSForm, and the standalone audit tool ``analysis/tools/session_history_order_audit.py``.
This module is the canonical implementation for the offline analysis pipeline.

Rules 1-6 of the dictionary (section 6.3) are enforced by ``validate_code``.
Rule 8 -- agreement between the decoded order, the order persisted by the
application and the order reconstructed from the events -- is enforced by
``compare_with_stored_order``.
"""

from __future__ import annotations

from typing import Iterable, Sequence

SCHEME = "order_checksum_v1"

ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"
CHECKSUM_WEIGHTS = (3, 5, 7, 11, 13)
CHECKSUM_OFFSET = 17
CODE_LENGTH = 6

ORDER_BY_PREFIX = {
    "A": ("C00", "C10", "C11"),
    "B": ("C00", "C11", "C10"),
    "C": ("C10", "C00", "C11"),
    "D": ("C10", "C11", "C00"),
    "E": ("C11", "C00", "C10"),
    "F": ("C11", "C10", "C00"),
}

SHORT_BY_ID = {
    "C00_robot_off_voice_off": "C00",
    "C10_robot_on_voice_off": "C10",
    "C11_robot_on_voice_on": "C11",
    "C00": "C00",
    "C10": "C10",
    "C11": "C11",
}


def calculate_checksum(payload: str) -> str:
    """Return the control character for the first five characters of a code."""
    if len(payload) != CODE_LENGTH - 1:
        raise ValueError("Checksum input must contain exactly five characters.")
    indexes = [ALPHABET.index(char) for char in payload]
    total = sum(weight * index for weight, index in zip(CHECKSUM_WEIGHTS, indexes))
    return ALPHABET[(total + CHECKSUM_OFFSET) % len(ALPHABET)]


def normalize_code(code: object) -> str:
    """Trim and upper-case a raw code value; never raises."""
    return str(code or "").strip().upper()


def validate_code(code: object) -> tuple[bool, str, tuple[str, ...]]:
    """Validate a questionnaire code.

    Returns ``(is_valid, error_reason, decoded_order)``. ``decoded_order`` is the
    permutation of short condition codes encoded by the prefix, or an empty tuple
    when the code is not valid.
    """
    normalized = normalize_code(code)
    if not normalized:
        return False, "missing", ()
    if len(normalized) != CODE_LENGTH:
        return False, "invalid_length", ()
    if any(char not in ALPHABET for char in normalized):
        return False, "invalid_alphabet", ()
    if normalized[0] not in ORDER_BY_PREFIX:
        return False, "invalid_prefix", ()
    if calculate_checksum(normalized[:5]) != normalized[5]:
        return False, "checksum_invalid", ()
    return True, "", ORDER_BY_PREFIX[normalized[0]]


def canonical_order(values: Iterable[object] | object) -> tuple[str, ...]:
    """Map raw condition identifiers to their short codes, preserving order."""
    if values is None:
        return ()
    if isinstance(values, str):
        candidates: Sequence[object] = [
            part for part in values.replace(";", " ").replace(",", " ").split() if part
        ]
    else:
        try:
            candidates = list(values)
        except TypeError:
            return ()
    result: list[str] = []
    for value in candidates:
        short = SHORT_BY_ID.get(str(value).strip(), "")
        if short and short not in result:
            result.append(short)
    return tuple(result)


def compare_with_stored_order(
    code: object,
    stored_order: Iterable[object] | object,
) -> tuple[bool, str, tuple[str, ...], tuple[str, ...]]:
    """Compare the order decoded from the code against the persisted order.

    Returns ``(matches, issue, decoded_order, stored_order_short)``. ``matches`` is
    ``True`` only when the code is valid, a stored order is available and both
    agree. When either side is missing the issue explains why the comparison could
    not be made, and ``matches`` is ``False``.
    """
    valid, error, decoded = validate_code(code)
    stored = canonical_order(stored_order)
    if not valid:
        return False, f"questionnaire_code_{error}", decoded, stored
    if not stored:
        return False, "stored_order_missing", decoded, stored
    if decoded != stored:
        return False, "code_prefix_order_mismatch", decoded, stored
    return True, "", decoded, stored


def self_test() -> None:
    """Verify the implementation against fixed vectors. Raises on failure."""
    for prefix, expected in ORDER_BY_PREFIX.items():
        payload = prefix + "23456"[:4]
        code = payload + calculate_checksum(payload)
        valid, error, decoded = validate_code(code)
        assert valid, f"generated code rejected: {code} ({error})"
        assert decoded == expected, f"decoded {decoded} != {expected} for prefix {prefix}"

    sample = "A2345"
    good = sample + calculate_checksum(sample)
    assert validate_code(good.lower())[0], "normalization must accept lower case"
    assert validate_code(good + "X")[1] == "invalid_length"
    assert validate_code("")[1] == "missing"
    assert validate_code("Z" + good[1:])[1] in {"invalid_prefix", "checksum_invalid"}
    assert validate_code("A0345" + "X")[1] == "invalid_alphabet", "0 is not in the alphabet"

    wrong_checksum = ALPHABET[(ALPHABET.index(good[5]) + 1) % len(ALPHABET)]
    assert validate_code(good[:5] + wrong_checksum)[1] == "checksum_invalid"

    matches, issue, decoded, stored = compare_with_stored_order(
        good, ["C00_robot_off_voice_off", "C10_robot_on_voice_off", "C11_robot_on_voice_on"]
    )
    assert matches, f"expected order match, got {issue} ({decoded} vs {stored})"

    matches, issue, _, _ = compare_with_stored_order(
        good, ["C11_robot_on_voice_on", "C00_robot_off_voice_off", "C10_robot_on_voice_off"]
    )
    assert not matches and issue == "code_prefix_order_mismatch"

    matches, issue, _, _ = compare_with_stored_order(good, [])
    assert not matches and issue == "stored_order_missing"
