chcp 65001
setlocal

if not exist extract\ (
  echo [ERROR] 未找到目录: %CD%\extract
  exit /b 1
)

set "TOOLSDIR=%~dp0"

python %TOOLSDIR%tbl.py e extract modified utf8\TBL.json
python %TOOLSDIR%textjson.py e Raw\TXT Raw\RE_TXT utf8\剧情文本
python %TOOLSDIR%asb.py e Raw\RE_TXT modified\adv\scn
python %TOOLSDIR%roll.py e utf8\staff modified\adv
python %TOOLSDIR%demo.py e extract utf8\战斗对话 modified