-- ============================================================
-- MoonShotChallenge (MSC) スキーマ
-- 2〜4 曲を 1 クレ通しで弾き、合計スコアを競うイベント。
-- 楽曲は管理者が登録 (公募制ではない)。
-- WeePA と完全独立 (FK・名前空間が衝突しない 'msc_' prefix)。
--
-- このファイルは PostgreSQL の docker-entrypoint-initdb.d で初回 init 時に
-- 自動実行される。既存 DB に追加適用したい場合は:
--   docker compose exec -T db psql -U <user> -d <db> < db/init/02_msc_schema.sql
-- 既存 weepa_* テーブル/データには一切触れない (CREATE のみ)。
-- ============================================================

-- ============================================
-- msc_events (MSC イベント本体)
-- - 楽曲公募制ではないので管理者が直接 INSERT
-- - WeePA と同じく tallied_at で公開状態を表現
--   (NULL=未公開 / 値あり=公開済)
-- ============================================
CREATE TABLE msc_events (
    msc_event_id    SERIAL PRIMARY KEY,
    title           TEXT NOT NULL,                       -- 例: "MSC vol.1 春の陣"
    description     TEXT,                                 -- 任意の説明文
    start_date      TIMESTAMPTZ NOT NULL,
    end_date        TIMESTAMPTZ NOT NULL,
    tallied_at      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT msc_events_valid_date_range CHECK (end_date > start_date)
);
CREATE INDEX idx_msc_events_period ON msc_events (start_date, end_date);

-- ============================================
-- msc_songs (MSC イベントに含まれる課題曲、2〜4 曲)
-- - song_order = クレジット内の必修順 (1..4)
-- - 件数 2〜4 の制約は DB では強制せず app 側で validate
--   (msc_events 保存時に msc_songs を 2〜4 件 INSERT)
-- ============================================
CREATE TABLE msc_songs (
    msc_event_id    INTEGER  NOT NULL REFERENCES msc_events(msc_event_id) ON DELETE CASCADE,
    song_order      SMALLINT NOT NULL CHECK (song_order BETWEEN 1 AND 4),
    song            TEXT     NOT NULL,
    effect          TEXT     NOT NULL,                   -- NOV/ADV/EXH/.../ULT

    PRIMARY KEY (msc_event_id, song_order)
);

-- ============================================
-- msc_submissions (ユーザーの 1 回の挑戦記録 = 1 クレ通し)
-- - x_id ごとに submit_id 1, 2, 3... を BEFORE INSERT トリガで自動採番
-- - approval_status: -1=却下 / 0=未承認 / 1=承認済 (提出単位)
-- - platform: 'arcade' (リザルト 1 枚) / 'konaste' (最大 5 枚)
-- ============================================
CREATE TABLE msc_submissions (
    msc_event_id    INTEGER NOT NULL REFERENCES msc_events(msc_event_id) ON DELETE CASCADE,
    x_id            TEXT    NOT NULL,
    submit_id       INTEGER NOT NULL,
    submit_name     TEXT    NOT NULL,
    platform        TEXT    NOT NULL CHECK (platform IN ('arcade', 'konaste')),
    submit_date     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    approval_status SMALLINT    NOT NULL DEFAULT 0,

    PRIMARY KEY (msc_event_id, x_id, submit_id)
);
CREATE INDEX idx_msc_submissions_status ON msc_submissions (approval_status, submit_date);

CREATE OR REPLACE FUNCTION msc_submissions_assign_submit_id()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.submit_id IS NULL THEN
        SELECT COALESCE(MAX(submit_id), 0) + 1 INTO NEW.submit_id
        FROM msc_submissions
        WHERE msc_event_id = NEW.msc_event_id AND x_id = NEW.x_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER msc_submissions_before_insert
    BEFORE INSERT ON msc_submissions
    FOR EACH ROW EXECUTE FUNCTION msc_submissions_assign_submit_id();

-- ============================================
-- msc_scores (各 play のスコア。1 提出につき N〜4 行)
-- - play_order = 1..4 (1 クレ内の n 曲目)
-- - song_order = どの event 曲を選んだか (1..N, msc_songs に存在する値のみ)
-- - 必修順序 (play_order 1..N は song_order = play_order と一致) は
--   trigger で担保。N+1..4 はリトライ枠で自由選択
-- - 合計スコアは SUM(MAX(score) GROUP BY song_order) で算出
-- ============================================
CREATE TABLE msc_scores (
    msc_event_id    INTEGER  NOT NULL,
    x_id            TEXT     NOT NULL,
    submit_id       INTEGER  NOT NULL,
    play_order      SMALLINT NOT NULL CHECK (play_order BETWEEN 1 AND 4),
    song_order      SMALLINT NOT NULL CHECK (song_order BETWEEN 1 AND 4),
    score           INTEGER  NOT NULL CHECK (score BETWEEN 0 AND 10000000),

    PRIMARY KEY (msc_event_id, x_id, submit_id, play_order),
    FOREIGN KEY (msc_event_id, x_id, submit_id)
        REFERENCES msc_submissions(msc_event_id, x_id, submit_id) ON DELETE CASCADE,
    FOREIGN KEY (msc_event_id, song_order)
        REFERENCES msc_songs(msc_event_id, song_order)
);
CREATE INDEX idx_msc_scores_event_song ON msc_scores (msc_event_id, song_order);

-- play_order が必修枠 (1..N) に入っているなら song_order = play_order を強制
CREATE OR REPLACE FUNCTION msc_scores_validate_order()
RETURNS TRIGGER AS $$
DECLARE
    song_count INTEGER;
BEGIN
    SELECT COUNT(*) INTO song_count
    FROM msc_songs
    WHERE msc_event_id = NEW.msc_event_id;

    IF NEW.play_order <= song_count AND NEW.song_order <> NEW.play_order THEN
        RAISE EXCEPTION
            'play_order % は必修枠 (1..%) のため song_order = % を要求 (got %)',
            NEW.play_order, song_count, NEW.play_order, NEW.song_order;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER msc_scores_before_insert_or_update
    BEFORE INSERT OR UPDATE ON msc_scores
    FOR EACH ROW EXECUTE FUNCTION msc_scores_validate_order();

-- ============================================
-- msc_submission_images (リザルト画像)
-- - play_order = 0 → 通しリザルト (arcade はこの 1 枚のみ。konaste の summary)
-- - play_order = 1..4 → その play のリザルト (konaste の各曲画像。msc_scores と同 play_order に対応)
-- ============================================
CREATE TABLE msc_submission_images (
    msc_event_id    INTEGER  NOT NULL,
    x_id            TEXT     NOT NULL,
    submit_id       INTEGER  NOT NULL,
    play_order      SMALLINT NOT NULL CHECK (play_order BETWEEN 0 AND 4),
    image_path      TEXT     NOT NULL,

    PRIMARY KEY (msc_event_id, x_id, submit_id, play_order),
    FOREIGN KEY (msc_event_id, x_id, submit_id)
        REFERENCES msc_submissions(msc_event_id, x_id, submit_id) ON DELETE CASCADE
);
