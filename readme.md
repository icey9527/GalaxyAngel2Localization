<!-- 语言切换 -->
<div style="text-align:right; margin-bottom:20px;">
  <button onclick="setLang('en')">English</button>
  <button onclick="setLang('zh')">中文</button>
</div>

<!-- 英语内容 -->
<div id="en">
  <p><strong>Note:</strong> This is a localization tool for <strong>Galaxy Angel 2</strong>. If you only want to extract resource files, please use the more convenient <a href="https://github.com/icey9527/Verviewer">VerViewer</a>.</p>
  
  <p>Most files that need modification for GA2 are in: <strong>ADV.DAT</strong>, <strong>ALL.DAT</strong>, <strong>SLG.DAT</strong></p>
  
  <p>To edit PSS videos, also check: 
    <input type="checkbox" id="strm_en1" checked> <label for="strm_en1"><strong>STRM_R.DAT</strong></label>
    <input type="checkbox" id="strm_en2" checked> <label for="strm_en2"><strong>STRM_R2.DAT</strong></label>
  </p>
</div>

<!-- 中文内容 -->
<div id="zh" style="display:none;">
  <p><strong>注意：</strong>这是《银河天使2》的汉化工具，如果你只想提取资源文件，请使用更方便的<a href="https://github.com/icey9527/Verviewer">VerViewer</a>。</p>
  
  <p>GA2大部分需要修改的文件都在：<strong>ADV.DAT</strong>、<strong>ALL.DAT</strong>、<strong>SLG.DAT</strong></p>
  
  <p>如果你还想编辑PSS视频可以勾选：
    <input type="checkbox" id="strm_zh1" checked> <label for="strm_zh1"><strong>STRM_R.DAT</strong></label>
    <input type="checkbox" id="strm_zh2" checked> <label for="strm_zh2"><strong>STRM_R2.DAT</strong></label>
  </p>
</div>

<script>
function setLang(lang) {
  document.getElementById('en').style.display = lang === 'en' ? 'block' : 'none';
  document.getElementById('zh').style.display = lang === 'zh' ? 'block' : 'none';
  localStorage.setItem('lang', lang);
}

// 默认英语
window.onload = () => setLang(localStorage.getItem('lang') || 'en');
</script>