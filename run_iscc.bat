@echo off
"C:\Users\32272\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "d:\AIfootball\installer\AIfootball.iss" > "d:\AIfootball\iscc_log.txt" 2>&1
echo ISCC_EXIT=%ERRORLEVEL%
