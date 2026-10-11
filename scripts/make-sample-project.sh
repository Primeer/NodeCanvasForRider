#!/usr/bin/env bash
# Builds an openable Unity-like solution from testData/project for manual checks in `./gradlew runIde`:
# Rider (and its Unity plugin) treat a folder as a Unity project when it has Assets/, ProjectSettings/ProjectVersion.txt,
# Library/ and a solution file. C# references UnityEngine.dll from a local Unity install (UNITY_MANAGED overrides it).
#
#   scripts/make-sample-project.sh [target-dir]      (default: build/SampleUnityProject)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
target="${1:-$root/build/SampleUnityProject}"
name="$(basename "$target")"

managed="${UNITY_MANAGED:-}"
if [[ -z "$managed" ]]; then
  for dir in /Applications/Unity/Hub/Editor/*/Unity.app/Contents/Managed "$HOME"/Unity/Hub/Editor/*/Editor/Data/Managed; do
    [[ -f "$dir/UnityEngine.dll" ]] && managed="$dir"
  done
fi
if [[ -z "$managed" ]]; then
  echo "UnityEngine.dll not found; set UNITY_MANAGED to the editor's Managed folder" >&2
  exit 1
fi

rm -rf "$target"
mkdir -p "$target"
cp -R "$root/testData/project/." "$target/"
mkdir -p "$target/Library"

# Force Text serialization, as asset indexing in the Unity plugin expects
cat > "$target/ProjectSettings/EditorSettings.asset" <<'EOF'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!159 &1
EditorSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 11
  m_SerializationMode: 2
EOF

# Attributes the stubs mention but do not declare (global namespace, so every stub namespace sees them)
cat > "$target/Assets/Stubs/SampleExtraStubs.cs" <<'EOF'
using System;

public class NameAttribute : Attribute { public NameAttribute(string name) { } }
public class DescriptionAttribute : Attribute { public DescriptionAttribute(string text) { } }
public class BlackboardOnlyAttribute : Attribute { }
public class fsSerializeAsAttribute : Attribute { public fsSerializeAsAttribute(string name) { } }
public class SerializedMethodInfo { }
EOF

# The stubs describe the serialized surface only; let literal defaults convert to BBParameter<T> so they compile
sed -i.bak 's/public class BBParameter<T> : BBParameter { }/public class BBParameter<T> : BBParameter { public static implicit operator BBParameter<T>(T value) { return new BBParameter<T>(); } }/' \
  "$target/Assets/Stubs/NodeCanvasStubs.cs" && rm -f "$target/Assets/Stubs/NodeCanvasStubs.cs.bak"

cat > "$target/Assembly-CSharp.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <EnableDefaultItems>false</EnableDefaultItems>
    <AssemblyName>Assembly-CSharp</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Assets/**/*.cs" />
    <Reference Include="UnityEngine" HintPath="$managed/UnityEngine.dll" />
  </ItemGroup>
</Project>
EOF

(cd "$target" && dotnet new sln --format sln -n "$name" >/dev/null && dotnet sln "$name.sln" add Assembly-CSharp.csproj >/dev/null)
echo "$target/$name.sln"
