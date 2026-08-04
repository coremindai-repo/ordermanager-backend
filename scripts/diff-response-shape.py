#!/usr/bin/env python3
"""Diff the JSON field names of two documented endpoints in the API contract.

Why this exists
---------------
A JSON example in the contract reads as complete whether or not it is. When a new
endpoint returns a shape derived from an entity that is already documented elsewhere,
comparing the two by eye reliably misses fields — `GET /api/order-line-items` shipped
with `originatingOrderId`/`originatingOrderNumber` silently dropped, and three careful
readings had not caught it.

So: don't read, diff. Then write the delta into the contract as a closed list, so the
next reader is told what differs instead of having to infer it.

Usage
-----
    python scripts/diff-response-shape.py "GET /api/orders/{orderId}" "GET /api/order-line-items"
    python scripts/diff-response-shape.py A B --from lineItems

Headings are matched as substrings against the contract's `###` headings, so a
distinctive fragment is enough. Both directions are reported; neither is "wrong" on its
own — the point is that every entry is a deliberate decision you can name.

`--from <key>` scopes both sides to the subtree starting at that key, which is usually
what you want: comparing two whole sections drags in envelope fields (an order's
`billTo`, a list's `count`) that were never meant to correspond. To compare the line
item inside an order against the line item inside a list, pass `--from lineItems`.

Caveat: this reads the documented examples, not the code. It answers "do the docs agree
with each other", which is the gap it was written for. Confirm against a live response
before publishing — the contract can be uniformly wrong.
"""

import re
import sys
from pathlib import Path

CONTRACT = Path(__file__).resolve().parent.parent / "docs" / "API-INTERFACE-CONTRACT.md"


def sections(text):
    """Split the contract into (heading, body) pairs at `###` boundaries."""
    parts = re.split(r"^### ", text, flags=re.MULTILINE)[1:]
    return [(p.split("\n", 1)[0].strip(), p) for p in parts]


def find(secs, needle):
    matches = [(h, b) for h, b in secs if needle.lower() in h.lower()]
    if not matches:
        sys.exit(f"No heading matching {needle!r}. Try a shorter fragment.")
    if len(matches) > 1:
        names = "\n  ".join(h for h, _ in matches)
        sys.exit(f"{needle!r} matches several headings; be more specific:\n  {names}")
    return matches[0]


def fields(body, scope=None):
    """Field names inside the section's fenced JSON blocks, in document order."""
    blocks = re.findall(r"```json\n(.*?)```", body, flags=re.DOTALL)
    if not blocks:
        return []
    text = "\n".join(blocks)
    if scope:
        marker = f'"{scope}"'
        if marker not in text:
            sys.exit(f"--from {scope!r} not found in one of the sections.")
        text = text[text.index(marker) + len(marker):]
    names = re.findall(r'"(\w+)"\s*:', text)
    return list(dict.fromkeys(names))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    scope = None
    if "--from" in sys.argv:
        i = sys.argv.index("--from")
        if i + 1 >= len(sys.argv):
            sys.exit("--from needs a key name.")
        scope = sys.argv[i + 1]
        args = [a for a in args if a != scope]

    if len(args) != 2:
        sys.exit(__doc__)

    secs = sections(CONTRACT.read_text(encoding="utf-8"))
    head_a, body_a = find(secs, args[0])
    head_b, body_b = find(secs, args[1])
    a, b = fields(body_a, scope), fields(body_b, scope)

    if not a or not b:
        sys.exit("One of those sections has no ```json block to compare.")

    only_a = [f for f in a if f not in b]
    only_b = [f for f in b if f not in a]

    print(f"A: {head_a}  ({len(a)} fields)")
    print(f"B: {head_b}  ({len(b)} fields)")
    if scope:
        print(f"scoped to the {scope!r} subtree")
    print()
    print(f"In A, absent from B ({len(only_a)}): {', '.join(only_a) or 'none'}")
    print(f"In B, absent from A ({len(only_b)}): {', '.join(only_b) or 'none'}")

    if only_a or only_b:
        # Deliberately ASCII: this runs in the Windows console, where a stray em-dash
        # comes out as a replacement character and makes the output look broken.
        print(
            "\nEvery name above must be a decision you can state. If one is a surprise,"
            "\nit is an omission rather than a trim - fix the endpoint. Otherwise write"
            "\nthe delta into the contract as a closed list, so nobody need run this again."
        )
    else:
        print("\nIdentical field sets.")


if __name__ == "__main__":
    main()
