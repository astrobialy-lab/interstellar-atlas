- connect the headset to the pc, and "enable" acces from the VR popup
- run step 2 to create the tars
- run step 3 to update apk + copy tars (if the app still doesnt has Data)
- while install apk is running, dont close/open unity, because it will reset the session. (if its open then keep it open)

# debug steps

## git bash:
```bash
ADB='/c/Program Files/Unity/Hub/Editor/6000.4.9f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe'
DST='/sdcard/Android/obb/com.TechnionStarFormation.InterstellarAtlas'

# right before opening the app:
"$ADB" logcat -c
# after openning the app:
"$ADB" logcat -d -v threadtime > VRSet.log
# and then in the logs you can search for " E Unity "

# checking the files
"$ADB" shell "ls $DST"
```

## powershell:
```powershell
(ask chatgpt)
```