# Use a lightweight Python image
FROM python:3.11-slim

# Set the working directory inside the container
WORKDIR /app

# Copy the requirements file and install dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy all the Python code into the container
COPY . .

# Run the FastAPI server (pointing to the app/main.py file)
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]