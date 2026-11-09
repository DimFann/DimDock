# Use slim Python base image
FROM python:3.11-slim

# Avoid interactive prompts during package install
ENV DEBIAN_FRONTEND=noninteractive

# Install system dependencies
RUN apt-get update && \
    apt-get install -y \
        bash \
        netcat-openbsd \
        libjpeg-dev \
        zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*

# Install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Set working directory
WORKDIR /app

# Copy Python script and wait-for-db script
COPY load_paths.py .
COPY file_store.py .
COPY linode_file_store.py .
COPY storage_smoke.py .
COPY storage_clean.py .
COPY storage_cors.py .
COPY build_shortcuts.py .
COPY db-load-activity.sh .

# Make wait script executable
#RUN chmod +x wait-for-db.sh
RUN chmod +x db-load-activity.sh

# Wait for DB before running Python script
ENTRYPOINT ["bash", "/app/db-load-activity.sh"]
