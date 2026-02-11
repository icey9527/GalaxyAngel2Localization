@echo off 
setlocal
chcp 65001

if not exist extract\ (
  echo [ERROR] 未找到目录: %CD%\extract
  exit /b 1
)

set "TOOLSDIR=%~dp0"

del badchars.txt
python %TOOLSDIR%tbl.py e extract modified utf8\TBL.json
python %TOOLSDIR%textjson_EN.py e Raw\TXT Raw\RE_TXT "utf8\Story Text"
python %TOOLSDIR%asb.py e Raw\RE_TXT modified\adv\scn
python %TOOLSDIR%roll.py e utf8\staff modified\adv
python %TOOLSDIR%demo.py e extract "utf8\Battle Text" modified