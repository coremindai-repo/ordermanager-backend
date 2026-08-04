#!/usr/bin/env python3
"""Diff this repo's API-INTERFACE-CONTRACT.md against the mobile repo's copy.

Why this exists
----------------
The two copies "must stay identical" (docs/ORIENTATION.md), but nothing enforces that —
syncing is a manual, per-instance step (CLAUDE.md §2), so it is easy for a contract
change to land here and simply never make it into the mobile repo. That is exactly what
happened with the GET /api/outsourcing-requests response shape in Epic 6: it shipped
here, mobile built against a defensively-inferred shape instead, and nobody noticed
until the mismatch surfaced downstream.

This does not fix the process gap by itself — syncing is still manual and still needs
explicit authorization each time (CLAUDE.md §2's exception). What it fixes is the
*noticing*: run it after any contract change, before calling that change "done".

Usage
-----
    python scripts/check-mobile-contract-sync.py
    python scripts/check-mobile-contract-sync.py <path-to-mobile-repo-or-its-contract-file>

With no argument, it looks for a sibling checkout named `ordermanagement-mobile` or
`order-management-mobile` next to this repo. Pass a path explicitly if your checkout
lives somewhere else, or if this only ever runs on a machine where you (not a Claude
Code session) have access to both repos.

Caveat: only tells you the two files differ, and where — same "docs agreeing with docs"
scope as scripts/diff-response-shape.py. It says nothing about whether either side
actually matches the deployed API.
"""

import difflib
import sys
from pathlib import Path

# The diff below can contain the contract's actual content — em dashes, arrows — which
# crashes on Windows' default console codepage (cp1252) rather than printing gracefully.
# Reconfigure instead of stripping the diff down to ASCII: this is real content, not the
# script's own messaging (which scripts/diff-response-shape.py keeps ASCII deliberately).
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BACKEND_CONTRACT = Path(__file__).resolve().parent.parent / "docs" / "API-INTERFACE-CONTRACT.md"

DEFAULT_MOBILE_REPO_NAMES = ["ordermanagement-mobile", "order-management-mobile"]


def find_mobile_contract(explicit):
    if explicit:
        p = Path(explicit)
        if p.is_dir():
            p = p / "docs" / "API-INTERFACE-CONTRACT.md"
        return p

    projects_dir = Path(__file__).resolve().parent.parent.parent
    for name in DEFAULT_MOBILE_REPO_NAMES:
        candidate = projects_dir / name / "docs" / "API-INTERFACE-CONTRACT.md"
        if candidate.exists():
            return candidate
    return None


def main():
    explicit = sys.argv[1] if len(sys.argv) > 1 else None
    mobile = find_mobile_contract(explicit)

    if mobile is None or not mobile.exists():
        sys.exit(
            "Could not find the mobile repo's API-INTERFACE-CONTRACT.md.\n"
            "This only works when both repos are checked out on the same machine.\n"
            "Pass the mobile repo's path (or its contract file's path) explicitly:\n"
            "    python scripts/check-mobile-contract-sync.py <path>"
        )

    backend_text = BACKEND_CONTRACT.read_text(encoding="utf-8")
    mobile_text = mobile.read_text(encoding="utf-8")

    if backend_text == mobile_text:
        print(f"In sync: {mobile}\nmatches: {BACKEND_CONTRACT}")
        return

    diff = list(difflib.unified_diff(
        mobile_text.splitlines(),
        backend_text.splitlines(),
        fromfile=str(mobile),
        tofile=str(BACKEND_CONTRACT),
        lineterm="",
    ))

    print(f"OUT OF SYNC\n  mobile:  {mobile}\n  backend: {BACKEND_CONTRACT}\n")
    print("\n".join(diff))
    print(
        "\nThis is a manual sync (CLAUDE.md section 2) - copy the backend's version into"
        "\nthe mobile repo. A Claude Code session needs explicit per-instance"
        "\nauthorization to write to the mobile repo; you can always do it yourself."
    )
    sys.exit(1)


if __name__ == "__main__":
    main()
