#!/bin/bash
# macOS/Linux에서 Windows용 kakao-bomb.exe 빌드 (build.ps1과 같은 결과물)
# 필요: csc (brew install mono). .NET Framework 4.8 레퍼런스 어셈블리는 NuGet에서 자동으로 받음.
# 사용: ./build.sh 1.0.2   → dist/kakao-bomb.exe
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${1:-1.0.0}"
REFPKG=build/net48ref
REF=$REFPKG/build/.NETFramework/v4.8

rm -rf dist build/AssemblyInfo.cs
mkdir -p build dist
if [ ! -f "$REF/mscorlib.dll" ]; then
    curl -sL -o build/net48ref.nupkg \
        https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3
    unzip -oq build/net48ref.nupkg -d "$REFPKG"
fi

cat > build/AssemblyInfo.cs <<CS
using System.Reflection;
[assembly: AssemblyTitle("kakao-bomb")]
[assembly: AssemblyProduct("kakao-bomb")]
[assembly: AssemblyVersion("$VERSION.0")]
[assembly: AssemblyFileVersion("$VERSION.0")]
CS

refs=()
for dll in mscorlib System System.Core System.Drawing System.Windows.Forms UIAutomationClient UIAutomationTypes WindowsBase; do
    refs+=("-r:$REF/$dll.dll")
done

csc -nologo -noconfig -nostdlib -target:winexe -optimize+ -platform:anycpu -codepage:65001 \
    -out:dist/kakao-bomb.exe "${refs[@]}" KakaoBomb.cs build/AssemblyInfo.cs

echo "완료: windows/dist/kakao-bomb.exe ($VERSION)"
