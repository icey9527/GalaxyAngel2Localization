@echo off 
setlocal
chcp 65001

if not exist extract\ (
  echo [ERROR] 未找到目录: "%CD%\extract"
  exit /b 1
)

set "TOOLSDIR=%~dp0"

python %TOOLSDIR%tbl.py d extract Raw\TBL.json
python %TOOLSDIR%asb.py d extract\adv\scn Raw\TXT
python %TOOLSDIR%textjson.py d Raw\TXT Raw\asb extract
python %TOOLSDIR%roll.py d extract\adv Raw\staff
python %TOOLSDIR%demo.py d extract Raw\demo