"""Strict block-YAML reader adapted from the reviewed Contracts registry guard.

Selected step mappings are parsed, never found by raw-source command searches.
Unsupported YAML syntax and duplicate keys fail closed.
"""
import json
import re

class Invalid(ValueError):
    pass


def scalar(value):
    if not value or value.startswith(("&", "*", "!", "{", "[")):
        raise Invalid("unsupported YAML scalar")
    if value.startswith('"'):
        try:
            return json.loads(value)
        except ValueError as error:
            raise Invalid("unsupported quoted scalar") from error
    if value.startswith("'"):
        if not value.endswith("'"):
            raise Invalid("unterminated scalar")
        return value[1:-1].replace("''", "'")
    return value


def read_yaml(text):
    """Read block mappings/sequences and literal/folded strings, rejecting ambiguity."""
    lines = text.splitlines()
    tokens = []
    i = 0
    while i < len(lines):
        raw = lines[i]
        if "\t" in raw:
            raise Invalid("tabs are unsupported")
        if not raw.strip() or raw.lstrip().startswith("#"):
            i += 1
            continue
        indent = len(raw) - len(raw.lstrip())
        # Comments outside quotes only; quoted hashes remain literal.
        quote = None
        clean = []
        for char in raw.strip():
            if char in "\"'":
                quote = None if quote == char else char if quote is None else quote
            if char == "#" and quote is None:
                break
            clean.append(char)
        value = "".join(clean).rstrip()
        if value in ("---", "...") or not value:
            raise Invalid("unsupported YAML document")
        if value.endswith((": |", ": |-", ": >", ": >-")):
            folded = value.endswith((": >", ": >-"))
            key = value.rsplit(":", 1)[0]
            body = []
            i += 1
            while i < len(lines) and (not lines[i].strip() or len(lines[i]) - len(lines[i].lstrip()) > indent):
                body.append(lines[i])
                i += 1
            nonempty = [len(line) - len(line.lstrip()) for line in body if line.strip()]
            width = min(nonempty) if nonempty else indent + 2
            parts = [line[width:] for line in body]
            # Chomping concerns trailing line breaks, not arbitrary command whitespace.
            while parts and not parts[-1].strip():
                parts.pop()
            if folded and any(not part.strip() or part.startswith((" ", "\t")) for part in parts):
                raise Invalid("unsupported folded paragraph or more-indented content")
            content = " ".join(parts) if folded else "\n".join(parts)
            if parts and not value.endswith("-"):
                content += "\n"  # Supported clip mode: | and > preserve one final line break.
            tokens.append((indent, key + ": " + json.dumps(content)))
            continue
        tokens.append((indent, value))
        i += 1

    def pair(value):
        match = re.fullmatch(r"([A-Za-z_][A-Za-z_0-9-]*):(?: (.*))?", value)
        if not match:
            raise Invalid("unsupported mapping syntax: " + value)
        return match.group(1), match.group(2)

    def block(index, indent):
        sequence = tokens[index][1].startswith("- ")
        result = [] if sequence else {}
        while index < len(tokens) and tokens[index][0] == indent:
            value = tokens[index][1]
            if sequence:
                if not value.startswith("- "):
                    raise Invalid("mixed sequence and mapping")
                if not re.match(r"[A-Za-z_][A-Za-z_0-9-]*:(?: |$)", value[2:]):
                    # Existing scalar sequences (e.g. service ports), never executable step mappings.
                    result.append(scalar(value[2:]))
                    index += 1
                    if index < len(tokens) and tokens[index][0] > indent:
                        raise Invalid("unsupported nested scalar sequence")
                    continue
                key, raw_value = pair(value[2:])
                item = {}
                index += 1
                if raw_value is None:
                    if index >= len(tokens) or tokens[index][0] <= indent + 2:
                        raise Invalid("missing nested sequence value")
                    item[key], index = block(index, tokens[index][0])
                else:
                    item[key] = scalar(raw_value)
                if index < len(tokens) and tokens[index][0] > indent:
                    if tokens[index][0] != indent + 2:
                        raise Invalid("unexpected sequence indentation")
                    extra, index = block(index, indent + 2)
                    if not isinstance(extra, dict) or key in extra:
                        raise Invalid("duplicate sequence key")
                    item.update(extra)
                result.append(item)
            else:
                key, raw_value = pair(value)
                if key in result:
                    raise Invalid("duplicate mapping key: " + key)
                index += 1
                if raw_value is None:
                    if index >= len(tokens) or tokens[index][0] <= indent:
                        result[key] = None
                    else:
                        result[key], index = block(index, tokens[index][0])
                else:
                    # Inline arrays/maps are opaque, permitted only outside selected steps.
                    result[key] = raw_value if raw_value.startswith(("[", "{")) else scalar(raw_value)
        if index < len(tokens) and tokens[index][0] > indent:
            raise Invalid("unexpected mapping indentation")
        return result, index

    if not tokens or tokens[0][0] != 0:
        raise Invalid("missing root mapping")
    result, end = block(0, 0)
    if end != len(tokens) or not isinstance(result, dict):
        raise Invalid("unsupported YAML root")
    return result

