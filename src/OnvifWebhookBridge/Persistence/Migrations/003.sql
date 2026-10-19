ALTER TABLE Rules ADD COLUMN IgnoreCertificateValidation INTEGER NOT NULL DEFAULT 0 CHECK(IgnoreCertificateValidation IN (0,1));
