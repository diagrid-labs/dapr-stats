# Importing Java SDK data into Postgres

1. Export the Java SDK data per month from SonarType.
2. Open the csv file in a text editor and a the header row:
   `package_name,collection_date,package_version,download_count,percentage,collection_over_number_of_days`
3. Add the package name ("dapr-sdk") as the first column in all records in the csv file.
4. Add the collection_date value (e.g. 2024-04-01) as the second column to all records in the csv file.
5. Add the collected_over_number_of_days value (the number of days in the month: 30) as the last column to all records in the csv file.
6. Save the file.
7. Use pgAdmin to import the csv file. Use the following settings:
   - Format: csv
   - Options: Header = 'true', Delimiter = ','
   - Columns to import: package_name, collection_date, package_version, download_count, percentage, collection_over_number_of_days