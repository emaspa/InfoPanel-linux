#!/usr/bin/env python3
"""Read the application version; an explicit version/tag must agree exactly."""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def version(value):
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
        raise ValueError(f"Expected X.Y.Z with no leading zeroes, got {value!r}")
    return value


def project_version():
    project = Path(__file__).resolve().parents[1] / "src/InfoPanel.App/InfoPanel.App.csproj"
    values = ET.parse(project).findall("./PropertyGroup/Version")
    if len(values) != 1:
        raise ValueError(f"{project}: expected exactly one <Version>")
    return version(values[0].text.strip())


if __name__ == "__main__":
    try:
        actual = project_version()
        if len(sys.argv) > 2:
            raise ValueError("Usage: packaging/version.py [X.Y.Z|vX.Y.Z]")
        expected = version(sys.argv[1].removeprefix("v")) if len(sys.argv) == 2 else actual
        if actual != expected:
            raise ValueError(f"VERSION MISMATCH: tag/request is {expected}, csproj <Version> is {actual}")
        print(actual)
    except (ValueError, ET.ParseError) as error:
        sys.exit(str(error))
