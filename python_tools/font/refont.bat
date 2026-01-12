@echo off & setlocal EnableDelayedExpansion
for /f "tokens=2 delims=: " %%a in ('chcp') do set "oldcp=%%a"
chcp 65001 >nul

set "BASE=I:\汉化\银河天使2"

set /a n=0
for /f "delims=" %%d in ('dir /b /ad ^| findstr /v /b /c:_') do set /a n+=1 & set "d[!n!]=%%d" & echo !n!. %%d
set /p i=选择:
set "sel=!d[%i%]!"

pushd "!sel!" || goto :done
del /q js.txt font.txt new.tbl SLPM_667.79 2>nul
python ..\tqjs.py "%BASE%\!sel!\utf8" js.txt
..\CharAdder js.txt font.txt /removeunicode:0000,2E7F /removeunicode:2E80,33FF /removeunicode:9FFF,FFFF
..\MappingGen ..\gasj.tbl font.txt font.tbl /fixcode:8140,889E
python ..\wtfont.py
popd

:done
chcp %oldcp% >nul
endlocal