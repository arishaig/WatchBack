FROM python:3.11-slim

# Standard *arr environment variables
ENV PUID=1000
ENV PGID=1000
ENV TZ=Etc/UTC

RUN groupadd -g $PGID abc && \
    useradd -u $PUID -g abc -m abc

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py .
RUN mkdir -p /app/static

# Ensure the app directory is owned by our user
RUN chown -R abc:abc /app

# Switch to the non-root user
USER abc

EXPOSE 8000

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]