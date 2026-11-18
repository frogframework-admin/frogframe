-- ESS (Employee Self-Service): raw attendance punches from the mobile application.
-- One row per punch attempt (clock-in / clock-out), including rejected punches (audit trail & fraud detection).
-- Written with a single lean INSERT (bypassing document hooks) to sustain morning/evening rush-hour peaks;
-- the Erp.EssAttendanceAggregation job subsequently materializes one Attendance document per employee per work date.
-- Version number 1001 (instead of 0xx) avoids colliding with future framework migrations.
CREATE TABLE ess_attendance_punches (
	id                uuid          PRIMARY KEY,          -- client-generated UUID: replaying the same ID never duplicates a punch
	tenant_id         text          NOT NULL,
	employee          text          NOT NULL,
	user_id           text          NOT NULL,
	punch_type        text          NOT NULL CHECK (punch_type IN ('in', 'out')),
	status            text          NOT NULL CHECK (status IN ('accepted', 'flagged', 'rejected')),
	reasons           text[]        NOT NULL DEFAULT '{}', -- rejection/flag reason codes (see docs/guides/ess-mobile-api.md)
	server_time       timestamptz   NOT NULL,             -- authoritative timestamp for online punches
	device_time       timestamptz,                        -- client device timestamp (used for offline punches)
	punch_time        timestamptz   NOT NULL,             -- effective punch timestamp
	work_date         date          NOT NULL,             -- work calendar date (night shift: clock-out after midnight inherits clock-in date)
	latitude          numeric(9,6),
	longitude         numeric(9,6),
	accuracy_m        numeric(8,1),
	location_code     text,                               -- nearest matched office/site location code
	distance_m        numeric(10,1),
	is_mock_location  boolean       NOT NULL DEFAULT false,
	is_offline        boolean       NOT NULL DEFAULT false,
	device_id         text,
	device_info       text,
	app_version       text,
	note              text,
	photo_file_id     text,                               -- verification selfie (frogframe_files), uploaded after punch
	attendance_name   text,                               -- materialized Attendance document name
	processed_at      timestamptz,                        -- NULL = pending aggregation by background job
	process_error     text,
	created_at        timestamptz   NOT NULL DEFAULT now()
);

CREATE INDEX ix_ess_punch_employee ON ess_attendance_punches (tenant_id, employee, work_date, punch_time);
CREATE INDEX ix_ess_punch_pending  ON ess_attendance_punches (tenant_id, work_date) WHERE processed_at IS NULL AND status <> 'rejected';
CREATE INDEX ix_ess_punch_time     ON ess_attendance_punches USING brin (server_time);

-- Raw punch attributes are immutable once recorded; only aggregation output and photo attachment columns may be updated.
CREATE FUNCTION ess_attendance_punches_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
	IF TG_OP = 'DELETE' THEN
		RAISE EXCEPTION 'ess_attendance_punches: attendance punches cannot be deleted';
	END IF;
	IF (NEW.id, NEW.tenant_id, NEW.employee, NEW.user_id, NEW.punch_type, NEW.status, NEW.reasons, NEW.server_time, NEW.device_time,
	    NEW.punch_time, NEW.work_date, NEW.latitude, NEW.longitude, NEW.accuracy_m, NEW.location_code, NEW.distance_m,
	    NEW.is_mock_location, NEW.is_offline, NEW.device_id, NEW.created_at)
	   IS DISTINCT FROM
	   (OLD.id, OLD.tenant_id, OLD.employee, OLD.user_id, OLD.punch_type, OLD.status, OLD.reasons, OLD.server_time, OLD.device_time,
	    OLD.punch_time, OLD.work_date, OLD.latitude, OLD.longitude, OLD.accuracy_m, OLD.location_code, OLD.distance_m,
	    OLD.is_mock_location, OLD.is_offline, OLD.device_id, OLD.created_at) THEN
		RAISE EXCEPTION 'ess_attendance_punches: punch attributes are immutable (only photo and aggregation fields may be updated)';
	END IF;
	RETURN NEW;
END $$;

CREATE TRIGGER ess_attendance_punches_guard BEFORE UPDATE OR DELETE ON ess_attendance_punches
	FOR EACH ROW EXECUTE FUNCTION ess_attendance_punches_guard();
