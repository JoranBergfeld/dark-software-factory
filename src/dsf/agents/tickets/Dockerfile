FROM python:3.12-slim

WORKDIR /app

COPY . /app

RUN pip install --no-cache-dir -e .

EXPOSE 8080

CMD ["uvicorn", "dsf.agents.tickets.main:app", "--host", "0.0.0.0", "--port", "8080"]
