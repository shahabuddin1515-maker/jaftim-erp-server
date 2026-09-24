/*
    Hangfire job storage lives in its OWN database on the same server so that its polling and job-state churn never
    competes with the business database (jaftim-live-db is 20 DTU and has already been pinned at 90%+ CPU by a job
    loop - README section 11.2).

    Hangfire creates its schema ([HangFire].*) itself on first start (PrepareSchemaIfNecessary = true) - nothing else
    to script. The SQL login used by Jaftim.Jobs needs db_owner on this database for the first run (schema creation);
    after that db_datareader/db_datawriter + EXECUTE is enough.

    Azure SQL: run on master.
*/
-- CREATE DATABASE [jaftim-uat-jobs] (EDITION = 'Basic');   -- UAT
-- CREATE DATABASE [jaftim-live-jobs] (EDITION = 'Standard', SERVICE_OBJECTIVE = 'S0');   -- Live

/* Connection string shape (Jobs + API, key "Hangfire"):
   Server=tcp:jaftim-uat-db-server.database.windows.net,1433;Initial Catalog=jaftim-uat-jobs;User ID=...;Password=...;Encrypt=True;
*/
