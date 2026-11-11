#!/usr/bin/env bash
# Builds publish/UCut-Tablet-<version>.apk without Gradle or the Google SDK download:
#   apt install android-sdk-platform-23 aapt dalvik-exchange zipalign apksigner openjdk
# The APK is signed with android/ucut.keystore (made on the first build; keep it, updates need the same key).
# UCUT_KEYSTORE can point to another keystore.
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ../Directory.Build.props)
CODE=$(echo "$VERSION" | awk -F. '{ print $1 * 10000 + $2 * 100 + $3 }')
SDK=${ANDROID_JAR:-/usr/lib/android-sdk/platforms/android-23/android.jar}
KEYSTORE=${UCUT_KEYSTORE:-ucut.keystore}
OUT=../publish
BUILD=build
rm -rf "$BUILD"
mkdir -p "$BUILD/gen" "$BUILD/classes" "$OUT"

sed -e "s/android:versionCode=\"[0-9]*\"/android:versionCode=\"$CODE\"/" \
    -e "s/android:versionName=\"[^\"]*\"/android:versionName=\"$VERSION\"/" AndroidManifest.xml > "$BUILD/AndroidManifest.xml"

aapt package -f -m -J "$BUILD/gen" -M "$BUILD/AndroidManifest.xml" -S res -I "$SDK"
javac -nowarn -Xlint:-options -source 8 -target 8 -encoding UTF-8 -bootclasspath "$SDK" -d "$BUILD/classes" \
      $(find src "$BUILD/gen" -name '*.java')
dalvik-exchange --dex --output="$BUILD/classes.dex" "$BUILD/classes"
aapt package -f -M "$BUILD/AndroidManifest.xml" -S res -A assets -I "$SDK" -F "$BUILD/unsigned.apk"
(cd "$BUILD" && aapt add unsigned.apk classes.dex > /dev/null)
zipalign -f 4 "$BUILD/unsigned.apk" "$BUILD/aligned.apk"

if [ ! -f "$KEYSTORE" ]; then
  keytool -genkeypair -keystore "$KEYSTORE" -storepass ucut-tablet -keypass ucut-tablet -alias ucut \
          -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=UCut, O=Uleimon" > /dev/null 2>&1
fi
apksigner sign --ks "$KEYSTORE" --ks-pass pass:ucut-tablet --key-pass pass:ucut-tablet --ks-key-alias ucut \
               --out "$OUT/UCut-Tablet-$VERSION.apk" "$BUILD/aligned.apk"
apksigner verify "$OUT/UCut-Tablet-$VERSION.apk"
rm -rf "$BUILD"
ls -la "$OUT/UCut-Tablet-$VERSION.apk"
