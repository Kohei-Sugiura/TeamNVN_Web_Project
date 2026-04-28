-- ============================================================
-- Weepa 初期化スキーマ
-- PostgreSQL の docker-entrypoint-initdb.d は、DBボリュームが
-- 空の場合（初回起動時）のみこのファイルを実行する。
-- 既存ボリュームがある状態で再適用したい場合は
--   docker compose down -v
-- でボリュームを削除してから compose up すること。
-- ============================================================

-- 楽曲名 trigram 検索（オートコンプリート）用
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ============================================
-- weepa_songs（楽曲投稿＝選曲提案）
-- approval_status: -1=却下 / 0=未承認 / 1=承認済み
-- 負値=終端（却下） / 非負=前進パイプライン。"非却下" は approval_status >= 0 で表現。
-- 「実施済み」は専用ステータスを持たず、weepa_events に紐付いているかで判定する
-- （e.g. WHERE NOT EXISTS (SELECT 1 FROM weepa_events e WHERE e.weepa_song_id = s.weepa_song_id)）。
-- schedule_order: 開催予定キュー（承認済み AND 未使用）の並び順。値が小さいほど早く開催。
--   - 0→1 (承認) 時に MAX+1 を付与（末尾追加）
--   - イベント作成時に NULL に戻して新規キューへの混入を防ぐ
--   - 並び替えは管理画面の「開催スケジュール」タブから行う
-- ============================================
CREATE TABLE weepa_songs (
    weepa_song_id   SERIAL PRIMARY KEY,
    song            TEXT NOT NULL,
    effect          TEXT NOT NULL,
    x_id            TEXT NOT NULL,
    submit_name     TEXT NOT NULL,
    submit_reason   TEXT,
    submit_date     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    approval_status SMALLINT NOT NULL DEFAULT 0,
    schedule_order  INTEGER
);

-- ============================================
-- weepa_events（開催イベント）
-- ============================================
CREATE TABLE weepa_events (
    weepa_event_id  SERIAL PRIMARY KEY,
    weepa_song_id   INTEGER NOT NULL REFERENCES weepa_songs(weepa_song_id),
    start_date      TIMESTAMPTZ NOT NULL,
    end_date        TIMESTAMPTZ NOT NULL,

    CONSTRAINT valid_date_range CHECK (end_date > start_date)
);

-- ============================================
-- weepa_scores（スコア投稿）
-- submit_id は BEFORE INSERT トリガーで自動採番するため
-- アプリ側は指定せず NULL で INSERT できる
-- approval_status: -1=却下 / 0=未承認 / 1=承認済み（ランキング対象）
-- ============================================
CREATE TABLE weepa_scores (
    weepa_event_id  INTEGER NOT NULL REFERENCES weepa_events(weepa_event_id),
    x_id            TEXT NOT NULL,
    submit_id       INTEGER NOT NULL,
    submit_name     TEXT NOT NULL,
    score           INTEGER NOT NULL CHECK (score BETWEEN 0 AND 10000000),
    ex_score        INTEGER,
    image_path      TEXT NOT NULL,
    submit_date     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    approval_status SMALLINT NOT NULL DEFAULT 0,

    PRIMARY KEY (weepa_event_id, x_id, submit_id)
);

-- ============================================
-- インデックス
-- ============================================
CREATE INDEX idx_weepa_songs_status
    ON weepa_songs (approval_status, submit_date);

CREATE INDEX idx_weepa_scores_status
    ON weepa_scores (approval_status, submit_date);

CREATE INDEX idx_weepa_scores_ranking
    ON weepa_scores (weepa_event_id, score DESC)
    WHERE approval_status = 1;

CREATE INDEX idx_weepa_events_period
    ON weepa_events (start_date, end_date);

CREATE INDEX idx_weepa_songs_name_trgm
    ON weepa_songs USING gin (song gin_trgm_ops);

-- ============================================
-- submit_id 自動採番トリガー
-- 同じ (weepa_event_id, x_id) 内で 1,2,3... と採番
-- 複合PK違反による同時実行衝突はアプリ側で1〜2回リトライ
-- ============================================
CREATE OR REPLACE FUNCTION weepa_scores_assign_submit_id()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.submit_id IS NULL THEN
        SELECT COALESCE(MAX(submit_id), 0) + 1
        INTO NEW.submit_id
        FROM weepa_scores
        WHERE weepa_event_id = NEW.weepa_event_id
          AND x_id = NEW.x_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_weepa_scores_submit_id
BEFORE INSERT ON weepa_scores
FOR EACH ROW
EXECUTE FUNCTION weepa_scores_assign_submit_id();
