# Claude Code ログイン ヘルパー
# Ymb Claude Usage で使用量ゲージを見るための OAuth ログイン支援スクリプト。
# claude_login.bat から呼ばれる。PowerShell 5.1 以上 / 7 対応。

$ErrorActionPreference = 'Continue'
$outputEncoding = [System.Text.Encoding]::UTF8

function Write-Line($text) { Write-Host $text }
function Write-Info($text)  { Write-Host "[INFO] $text" -ForegroundColor Cyan }
function Write-Ok($text)    { Write-Host "[OK] $text" -ForegroundColor Green }
function Write-Err($text)   { Write-Host "[ERROR] $text" -ForegroundColor Red }

Write-Host "============================================" -ForegroundColor Yellow
Write-Host "  Claude Code ログイン ヘルパー" -ForegroundColor Yellow
Write-Host "  Ymb Claude Usage で使用量ゲージを見る用" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Host ""

# ---- 1. claude の存在確認(なければインストール) ----
$claude = Get-Command claude -ErrorAction SilentlyContinue
if ($claude) {
    Write-Ok "claude が見つかりました ($($claude.Source))"
} else {
    Write-Info "claude が見つかりません。公式インストーラを実行します(初回のみ、1〜2分)..."
    $null = "irm https://claude.ai/install.ps1 | iex" | Invoke-Expression
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Info "公式インストーラに失敗。npm での代替を試します..."
        npm install -g @anthropic-ai/claude-code
    }
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Err "claude を起動できません。新しいターミナルで再実行してください"
        Read-Host "Enter で終了"
        exit 1
    }
}

# ---- 2. ログイン状態の確認 ----
Write-Host ""
Write-Host "---- 現在のログイン状態 ----" -ForegroundColor Yellow
claude auth status
Write-Host ""

# status を機械可読で再取得して判定
$statusJson = claude auth status --json 2>$null | Out-String
$loggedIn = $false
try {
    $s = $statusJson | ConvertFrom-Json
    $loggedIn = ($s.loggedIn -eq $true)
} catch {
    # パース失敗時は表示上の判定のみ(手動確認に任せる)
}

if ($loggedIn) {
    Write-Ok "すでにログイン済みです。このまま閉じて問題ありません。"
    Read-Host "Enter で終了"
    exit 0
}

Write-Host "未ログインのようです。続けてログインしますか？"
try {
    $ans = Read-Host "ログインするなら [Y] を入力(それ以外は終了)"
} catch {
    # 入力ストリームが閉じている(自動実行)場合は終了
    exit 0
}
if ($ans -notmatch '^[Yy]') { exit 0 }

# ---- 3. ログイン実行 ----
Write-Host ""
Write-Host "---- ブラウザで Anthropic のログイン画面を開きます ----" -ForegroundColor Yellow
Write-Host "  手順:"
Write-Host "    1. 開いたブラウザで Claude Pro / Max のアカウントで承認"
Write-Host "    2. 表示されたコードをコピーして、このターミナルに貼り付けて Enter"
Write-Host ""
claude auth login --claudeai
if ($LASTEXITCODE -ne 0) {
    Write-Err "ログイン処理に失敗しました。もう一度実行してください"
    Read-Host "Enter で終了"
    exit 1
}

# ---- 4. 結果確認 ----
Write-Host ""
Write-Host "---- ログイン結果 ----" -ForegroundColor Yellow
claude auth status

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  完了。Ymb Claude Usage を起動すれば" -ForegroundColor Green
Write-Host "  使用量ゲージが表示されます" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Read-Host "Enter で終了"
exit 0
