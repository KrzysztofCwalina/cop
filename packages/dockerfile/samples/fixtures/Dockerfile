# syntax=docker/dockerfile:1
FROM python:3.12-slim AS build
WORKDIR /src
COPY requirements.txt .
RUN pip install --no-cache-dir \
    -r requirements.txt

FROM node:latest
WORKDIR /app
COPY --from=build /src /app
USER node

FROM ubuntu
RUN apt-get update && apt-get install -y curl
CMD ["bash"]
